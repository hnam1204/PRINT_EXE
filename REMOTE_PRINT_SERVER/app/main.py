from datetime import datetime, timedelta, timezone
import secrets
import hashlib
import json
import time
import asyncio
import uuid

from fastapi import Depends, FastAPI, File, Header, HTTPException, Request, UploadFile, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, Response
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError, SQLAlchemyError

from .config import ConfigurationError, Settings
from .database import ActivationCode, AuditEvent, Device, Pairing, PairingCode, PrintAuthorization, PrintJob, PrintJobTombstone, Printer, RealtimeEvent, as_utc, create_session_factory, utcnow
from .schemas import (
    DeviceProfile,
    DeviceRegistration,
    DeviceRegistrationResponse,
    HeartbeatRequest,
    HeartbeatResponse,
    PairingDecision,
    PairingPrinterPermissionUpdate,
    PairingRequest,
    PairingResponse,
    PendingPairing,
    PairingSummary,
    PrintJobRequest,
    PrintJobResponse,
    PrinterRegistration,
    PrinterResponse,
    PairingCodeIssueRequest,
    PairingCodeResolveRequest,
    PairingCodeResolveResponse,
    PairingCodeResponse,
    JobAcknowledgeRequest,
    JobCancelRequest,
    JobClaimRequest,
    JobClaimResponse,
    JobRenewRequest,
    JobStatusUpdateRequest,
    GeofenceSettings,
    GeofenceResponse,
    PrintAuthorizationRequest,
    PrintAuthorizationResponse,
    PrintAuthorizationConsumeRequest,
    PrintAuthorizationConsumeResponse,
    RealtimeEventEnvelope,
    RealtimeSessionResponse,
)
from .pairing_codes import decrypt_pairing_code, hash_pairing_code, issue_pairing_code
from .cleanup import run_cleanup
from .geofence import verify_location
from .activation_policy import GEOFENCE_POLICY_LIMITED, GEOFENCE_POLICY_UNLIMITED
from .storage import BlobStore, VercelBlobStore
from .security import enforce_rate_limit, hash_token, issue_realtime_session, new_device_token, require_device, require_role, verify_device_reactivation_signature
from .telegram_bot import (
    create_activation_code,
    delete_message,
    parse_command,
    record_message,
    remove_tracked_message,
    purge_revoked_activation_codes,
    reissue_activation_code,
    revoke_activation_code,
    send_message,
    set_bot_commands,
    format_remaining,
    format_vietnam_datetime,
    tracked_message_ids,
)


def _revoke_pairings_for_code(session, host_id: str, code: str, revoked_at: datetime, enqueue_event=None) -> int:
    """Invalidate every pairing created from a rotated host code."""
    pairings = session.scalars(select(Pairing).where(
        Pairing.host_id == host_id,
        Pairing.code == code,
        Pairing.status.in_(["PENDING", "APPROVED"]),
    )).all()
    for pairing in pairings:
        pairing.status = "REVOKED"
        pairing.decided_at = revoked_at
        session.add(AuditEvent(
            actor_id=host_id,
            action="PAIRING_REVOKED_BY_CODE_ROTATION",
            target_type="PAIRING",
            target_id=pairing.id,
        ))
        if enqueue_event is not None:
            enqueue_event(
                session,
                "PAIRING_REVOKED",
                pairing.client_id,
                host_id=pairing.host_id,
                client_id=pairing.client_id,
                pairing_id=pairing.id,
                payload={
                    "pairingId": pairing.id,
                    "status": pairing.status,
                    "reason": "PAIRING_CODE_ROTATED",
                },
            )
    return len(pairings)


def _close_expired_live_pairings(session, host_id: str, client_id: str, now: datetime, enqueue_event=None) -> None:
    """Release the live-pairing uniqueness slot after its lease expires."""
    expired = session.scalars(
        select(Pairing).where(
            Pairing.host_id == host_id,
            Pairing.client_id == client_id,
            Pairing.status == "PENDING",
            Pairing.expires_at <= now,
        )
    ).all()
    for pairing in expired:
        pairing.status = "REVOKED"
        pairing.decided_at = pairing.decided_at or now
        session.add(AuditEvent(
            actor_id=host_id,
            action="PAIRING_REVOKED_BY_EXPIRY",
            target_type="PAIRING",
            target_id=pairing.id,
        ))
        if enqueue_event is not None:
            enqueue_event(
                session,
                "PAIRING_REVOKED",
                pairing.client_id,
                host_id=pairing.host_id,
                client_id=pairing.client_id,
                pairing_id=pairing.id,
                payload={
                    "pairingId": pairing.id,
                    "status": pairing.status,
                    "reason": "PAIRING_EXPIRED",
                },
            )


def create_app(settings: Settings | None = None, database_url: str | None = None, blob_store: BlobStore | None = None) -> FastAPI:
    settings = settings or Settings.from_env()
    db_url = database_url or settings.database_url
    engine, session_factory = create_session_factory(db_url, create_schema=db_url.startswith("sqlite"))

    app = FastAPI(
        title=settings.app_name,
        version=settings.api_version,
        docs_url=f"/api/{settings.api_version}/docs" if settings.environment != "production" else None,
        redoc_url=None,
    )
    app.state.settings = settings
    app.state.engine = engine
    app.state.session_factory = session_factory
    app.state.started_at = time.monotonic()
    if blob_store is None and (settings.blob_store_id or settings.blob_read_write_token):
        blob_store = VercelBlobStore()
    app.state.blob_store = blob_store

    def enqueue_realtime_event(
        session,
        event_type: str,
        recipient_device_id: str,
        *,
        host_id: str | None = None,
        client_id: str | None = None,
        pairing_id: str | None = None,
        job_id: str | None = None,
        payload: dict | None = None,
        correlation_id: str | None = None,
    ) -> None:
        event_correlation_id = correlation_id or str(uuid.uuid4())
        session.add(RealtimeEvent(
            event_type=event_type,
            recipient_device_id=recipient_device_id,
            host_id=host_id,
            client_id=client_id,
            pairing_id=pairing_id,
            job_id=job_id,
            correlation_id=event_correlation_id,
            payload_json=payload or {},
        ))
        # LISTEN/NOTIFY is only a wake-up signal. The durable row above remains
        # the source of truth and is replayed by cursor after reconnect.
        try:
            with session.begin_nested():
                session.execute(
                    text("SELECT pg_notify('rpm_realtime_events', :payload)"),
                    {"payload": json.dumps({"correlationId": event_correlation_id})},
                )
        except SQLAlchemyError:
            # SQLite and deployments without PostgreSQL notification support
            # continue using the gateway reconciliation path.
            pass

    def event_envelope(event: RealtimeEvent) -> RealtimeEventEnvelope:
        return RealtimeEventEnvelope(
            eventId=event.id,
            eventType=event.event_type,
            recipientDeviceId=event.recipient_device_id,
            hostId=event.host_id,
            clientId=event.client_id,
            pairingId=event.pairing_id,
            jobId=event.job_id,
            serverTimestamp=event.created_at,
            sequence=event.id,
            cursor=event.id,
            correlationId=event.correlation_id,
            payload=event.payload_json or {},
        )

    def job_response(job: PrintJob, claim_token: str | None = None):
        payload = PrintJobResponse(
            jobId=job.id,
            idempotencyKey=job.idempotency_key,
            clientId=job.client_id,
            hostId=job.host_id,
            printerId=job.printer_id,
            status=job.status,
            metadata=job.metadata_json or {},
            dataRef=job.data_ref,
            contentSha256=job.content_sha256,
            contentSize=job.content_size,
            contentType=job.content_type,
            claimExpiresAt=job.claim_expires_at,
            spoolerJobId=job.spooler_job_id,
            failureCode=job.failure_code,
            failureMessage=job.failure_message,
            completedAt=job.completed_at,
        )
        if claim_token is None:
            return payload
        return JobClaimResponse(**payload.model_dump(), claimToken=claim_token)

    def require_job_access(session, job_id: str, device: Device) -> PrintJob:
        job = session.get(PrintJob, job_id)
        if job is None or device.id not in {job.client_id, job.host_id}:
            raise HTTPException(status_code=404, detail="Print job not found")
        return job

    def require_claim(session, job: PrintJob, device: Device, claim_token: str) -> None:
        if device.role != "HOST" or job.host_id != device.id or not job.claim_token_hash:
            raise HTTPException(status_code=403, detail="Valid Host claim required")
        if not secrets.compare_digest(job.claim_token_hash, hash_token(claim_token)):
            raise HTTPException(status_code=409, detail="Claim token is no longer valid")
        if job.claim_expires_at and as_utc(job.claim_expires_at) <= utcnow() and job.status not in {"COMPLETED", "FAILED", "CANCELED"}:
            raise HTTPException(status_code=409, detail="Job claim has expired")

    def pairing_response(pairing: Pairing) -> PairingResponse:
        return PairingResponse(
            pairingId=pairing.id, hostId=pairing.host_id, clientId=pairing.client_id,
            code=pairing.code, status=pairing.status, expiresAt=pairing.expires_at,
            locationStatus=pairing.location_status,
            printerAccessMode=pairing.printer_access_mode or "ALL_SHARED",
        )

    def pairing_allows_printer(pairing: Pairing, printer_id: str) -> bool:
        mode = (pairing.printer_access_mode or "ALL_SHARED").upper()
        if mode == "NONE":
            return False
        if mode == "SELECTED_ONLY":
            return printer_id in (pairing.allowed_printers or [])
        # Legacy rows with a non-empty list were selected-only before the
        # explicit mode column existed.
        if pairing.allowed_printers:
            return printer_id in pairing.allowed_printers
        return True

    def verify_pairing_location(host: Device, activation: ActivationCode | None, payload: PairingRequest, now: datetime):
        if activation is not None and activation.geofence_policy == GEOFENCE_POLICY_UNLIMITED:
            return None
        if activation is not None and activation.geofence_policy == GEOFENCE_POLICY_LIMITED and not host.geofence_enabled:
            raise HTTPException(status_code=503, detail="Host geofence is not configured")
        if not host.geofence_enabled:
            return None
        if host.geofence_latitude is None or host.geofence_longitude is None:
            raise HTTPException(status_code=503, detail="Host geofence is not configured")
        result = verify_location(
            host_latitude=host.geofence_latitude,
            host_longitude=host.geofence_longitude,
            radius_meters=host.geofence_radius_meters,
            client_latitude=payload.latitude,
            client_longitude=payload.longitude,
            accuracy_meters=payload.accuracyMeters,
            location_timestamp=payload.locationTimestamp,
            now=now,
            max_accuracy_meters=settings.geofence_max_accuracy_meters,
            max_age_seconds=settings.geofence_max_age_seconds,
        )
        if result.status != "VERIFIED":
            raise HTTPException(status_code=403, detail=f"Location verification failed: {result.reason}")
        return result

    def location_payload(host_id: str, payload) -> PairingRequest:
        return PairingRequest(
            hostId=host_id,
            latitude=payload.latitude,
            longitude=payload.longitude,
            accuracyMeters=payload.accuracyMeters,
            locationTimestamp=payload.locationTimestamp,
        )

    def apply_location_evidence(pairing: Pairing, payload, result) -> None:
        pairing.client_latitude = payload.latitude
        pairing.client_longitude = payload.longitude
        pairing.client_accuracy_meters = payload.accuracyMeters
        pairing.client_location_at = payload.locationTimestamp
        pairing.location_status = result.status if result is not None else "NOT_REQUIRED"

    def require_approved_printer_access(session, client: Device, host_id: str, printer_id: str) -> tuple[Pairing, Printer, Device, ActivationCode | None]:
        pairing = session.scalar(select(Pairing).where(
            Pairing.host_id == host_id,
            Pairing.client_id == client.id,
            Pairing.status == "APPROVED",
        ))
        printer = session.get(Printer, printer_id)
        host = session.get(Device, host_id)
        if pairing is None or printer is None or host is None or host.role != "HOST" or host.is_revoked or printer.host_id != host_id or not printer.is_shared:
            raise HTTPException(status_code=403, detail="Approved shared printer access required")
        if pairing.allowed_printers and printer.id not in pairing.allowed_printers:
            raise HTTPException(status_code=403, detail="Printer is not allowed for this pairing")
        activation = session.get(ActivationCode, host.activation_code_id) if host.activation_code_id else None
        return pairing, printer, host, activation

    app.add_middleware(
        CORSMiddleware,
        allow_origins=list(settings.allowed_origins),
        allow_credentials=False,
        allow_methods=["GET", "POST", "PATCH", "DELETE", "OPTIONS"],
        allow_headers=["Authorization", "Content-Type", "Idempotency-Key", "X-Enrollment-Secret", "X-Admin-Key", "X-Device-Reactivation-Signature"],
    )

    @app.middleware("http")
    async def security_headers(request, call_next):
        response = await call_next(request)
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["X-Frame-Options"] = "DENY"
        response.headers["Referrer-Policy"] = "no-referrer"
        if settings.environment in {"production", "preview"}:
            response.headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains"
        return response

    @app.exception_handler(ConfigurationError)
    async def configuration_error_handler(_, exc):
        return JSONResponse(status_code=500, content={"error": {"code": "CONFIGURATION_ERROR", "message": str(exc)}})

    @app.get("/api/v1/health", tags=["Health"])
    def health():
        try:
            with app.state.engine.connect() as connection:
                connection.execute(text("SELECT 1"))
        except SQLAlchemyError:
            return JSONResponse(
                status_code=503,
                content={
                    "status": "degraded",
                    "apiVersion": settings.api_version,
                    "environment": settings.environment,
                    "dependencies": {"database": "unavailable"},
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                },
            )
        return {
            "status": "ok",
            "apiVersion": settings.api_version,
            "environment": settings.environment,
            "uptimeSeconds": round(time.monotonic() - app.state.started_at, 3),
            "dependencies": {"database": "ready"},
            "timestamp": datetime.now(timezone.utc).isoformat(),
        }

    @app.post("/api/v1/devices/register", response_model=DeviceRegistrationResponse, status_code=201, tags=["Devices"])
    def register_device(
        payload: DeviceRegistration,
        request: Request,
        enrollment_secret: str | None = Header(default=None, alias="X-Enrollment-Secret"),
        reactivation_signature: str | None = Header(default=None, alias="X-Device-Reactivation-Signature"),
    ):
        enrollment_secret = enrollment_secret.strip() if enrollment_secret else None
        if not enrollment_secret:
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Activation code is required")

        with session_factory() as session:
            enforce_rate_limit(
                session,
                f"device-register:{request.client.host if request.client else 'unknown'}",
                settings.rate_limit_max_attempts,
                settings.rate_limit_window_seconds,
            )
            device = session.scalar(select(Device).where(Device.id == payload.deviceId))
            uses_admin_secret = bool(
                settings.enrollment_secret
                and secrets.compare_digest(enrollment_secret, settings.enrollment_secret)
            )
            activation_code = None
            reactivating_revoked_device = False
            if not uses_admin_secret:
                activation_code = session.scalar(
                    select(ActivationCode)
                    .where(ActivationCode.code_hash == hash_token(enrollment_secret.upper()))
                    .with_for_update()
                )
                now = utcnow()
                if activation_code is None or activation_code.revoked_at is not None or (
                    activation_code.expires_at is not None and as_utc(activation_code.expires_at) <= now
                ):
                    raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid or expired activation code")
                if device is not None and device.is_revoked:
                    canonical_message = f"RPM-REACTIVATE:{payload.deviceId}:{enrollment_secret.upper()}"
                    if (
                        not device.public_key
                        or not payload.publicKey
                        or payload.publicKey != device.public_key
                        or not reactivation_signature
                        or not verify_device_reactivation_signature(device.public_key, reactivation_signature, canonical_message)
                    ):
                        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Device reactivation proof required")
                    reactivating_revoked_device = True
                    for pairing in session.scalars(select(Pairing).where(
                        ((Pairing.host_id == device.id) | (Pairing.client_id == device.id)),
                        Pairing.status.in_(["PENDING", "APPROVED"]),
                    )).all():
                        pairing.status = "REVOKED"
                        pairing.decided_at = now
                        session.add(pairing)
                elif device is not None and device.activation_code_id not in (None, activation_code.id):
                    raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Device is already activated with another code")
                elif device is not None and device.public_key and payload.publicKey != device.public_key:
                    raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="Device public key cannot change without reactivation")
                if device is None or device.activation_code_id is None:
                    if activation_code.max_devices is not None and activation_code.used_devices >= activation_code.max_devices:
                        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Activation code device limit reached")
                    activation_code.used_devices += 1

            token = new_device_token()
            if device is None:
                device = Device(
                    id=payload.deviceId,
                    name=payload.name,
                    role=payload.role,
                    app_version=payload.appVersion,
                    token_hash=hash_token(token),
                    activation_code_id=activation_code.id if activation_code is not None else None,
                    public_key=payload.publicKey,
                    key_algorithm=payload.keyAlgorithm,
                    last_seen_at=utcnow(),
                )
                if activation_code is not None and payload.role == "HOST" and activation_code.geofence_radius_meters is not None:
                    device.geofence_radius_meters = activation_code.geofence_radius_meters
                session.add(device)
            else:
                if device.is_revoked and not reactivating_revoked_device:
                    raise HTTPException(status_code=403, detail="Device is revoked")
                if reactivating_revoked_device:
                    device.is_revoked = False
                    device.activation_code_id = activation_code.id
                device.name = payload.name
                device.role = payload.role
                device.app_version = payload.appVersion
                device.token_hash = hash_token(token)
                if activation_code is not None and device.activation_code_id is None:
                    device.activation_code_id = activation_code.id
                if activation_code is not None and payload.role == "HOST" and activation_code.geofence_radius_meters is not None:
                    device.geofence_radius_meters = activation_code.geofence_radius_meters
                device.public_key = payload.publicKey
                device.key_algorithm = payload.keyAlgorithm
                device.last_seen_at = utcnow()

            session.add(AuditEvent(actor_id=payload.deviceId, action="DEVICE_REGISTERED", target_type="DEVICE", target_id=payload.deviceId))
            session.commit()
            activation = activation_code or (session.get(ActivationCode, device.activation_code_id) if device.activation_code_id else None)
            return DeviceRegistrationResponse(
                deviceId=device.id,
                deviceToken=token,
                role=device.role,
                isActivationCode=activation is not None,
                activationLabel=activation.label if activation else "Quản trị viên",
                activationExpiresAt=activation.expires_at if activation else None,
                activationMaxDevices=activation.max_devices if activation else None,
                activationUsedDevices=activation.used_devices if activation else None,
                activationGeofencePolicy=activation.geofence_policy if activation else None,
                activationGeofenceRadiusMeters=activation.geofence_radius_meters if activation else None,
            )

    @app.get("/api/v1/devices/me", response_model=DeviceProfile, tags=["Devices"])
    def get_device_profile(device: Device = Depends(require_device)):
        with session_factory() as session:
            activation = session.get(ActivationCode, device.activation_code_id) if device.activation_code_id else None
        return DeviceProfile(
            deviceId=device.id,
            name=device.name,
            role=device.role,
            appVersion=device.app_version,
            isRevoked=device.is_revoked,
            lastSeenAt=device.last_seen_at,
            publicKey=device.public_key,
            keyAlgorithm=device.key_algorithm,
            activationLabel=activation.label if activation else "Quản trị viên",
            activationExpiresAt=activation.expires_at if activation else None,
            activationMaxDevices=activation.max_devices if activation else None,
            activationUsedDevices=activation.used_devices if activation else None,
            activationGeofencePolicy=activation.geofence_policy if activation else None,
            activationGeofenceRadiusMeters=activation.geofence_radius_meters if activation else None,
        )

    @app.post("/api/v1/realtime/session", response_model=RealtimeSessionResponse, tags=["Realtime"])
    def create_realtime_session(device: Device = Depends(require_device)):
        if not settings.realtime_gateway_url or not settings.realtime_gateway_shared_secret:
            raise HTTPException(status_code=503, detail="Realtime gateway is not configured")
        token, expires_at = issue_realtime_session(device, settings.realtime_gateway_shared_secret)
        return RealtimeSessionResponse(
            sessionToken=token,
            gatewayUrl=settings.realtime_gateway_url,
            expiresAt=expires_at,
        )

    @app.post("/api/v1/devices/heartbeat", response_model=HeartbeatResponse, tags=["Devices"])
    def heartbeat(payload: HeartbeatRequest | None = None, device: Device = Depends(require_device)):
        with session_factory() as session:
            current = session.get(Device, device.id)
            if current is None or current.is_revoked:
                raise HTTPException(status_code=401, detail="Device is not active")
            if payload is not None and payload.appVersion:
                current.app_version = payload.appVersion
            current.last_seen_at = utcnow()
            session.add(AuditEvent(actor_id=current.id, action="DEVICE_HEARTBEAT", target_type="DEVICE", target_id=current.id))
            session.commit()
            return HeartbeatResponse(deviceId=current.id, status="ONLINE", lastSeenAt=current.last_seen_at)

    @app.post("/api/v1/pairings", response_model=PairingResponse, status_code=201, tags=["Pairing"])
    def create_pairing(payload: PairingRequest, device: Device = Depends(require_device)):
        require_role(device, "CLIENT")
        now = utcnow()
        expires_at = now + timedelta(minutes=settings.pairing_ttl_minutes)
        with session_factory() as session:
            host = session.get(Device, payload.hostId)
            if host is None or host.is_revoked:
                raise HTTPException(status_code=404, detail="Host not found")
            if host.role != "HOST":
                raise HTTPException(status_code=422, detail="Target device is not a Host")
            activation = session.get(ActivationCode, host.activation_code_id) if host.activation_code_id else None
            location_result = verify_pairing_location(host, activation, payload, now)
            _close_expired_live_pairings(session, host.id, device.id, now, enqueue_realtime_event)
            existing = session.scalar(
                select(Pairing)
                .where(
                    Pairing.client_id == device.id,
                    Pairing.host_id == host.id,
                    (Pairing.status == "APPROVED")
                    | ((Pairing.status == "PENDING") & (Pairing.expires_at > now)),
                )
                .order_by(Pairing.created_at.desc())
            )
            if existing:
                apply_location_evidence(existing, payload, location_result)
                session.commit()
                return pairing_response(existing)
            code = f"{secrets.randbelow(1_000_000):06d}"
            pairing = Pairing(
                host_id=host.id,
                client_id=device.id,
                code=code,
                status="PENDING",
                expires_at=expires_at,
            )
            apply_location_evidence(pairing, payload, location_result)
            try:
                with session.begin_nested():
                    session.add(pairing)
                    session.flush()
            except IntegrityError:
                existing = session.scalar(
                    select(Pairing)
                    .where(
                        Pairing.client_id == device.id,
                        Pairing.host_id == host.id,
                        (Pairing.status == "APPROVED")
                        | ((Pairing.status == "PENDING") & (Pairing.expires_at > now)),
                    )
                    .order_by(Pairing.created_at.desc())
                )
                if existing is None:
                    raise
                apply_location_evidence(existing, payload, location_result)
                session.commit()
                return pairing_response(existing)
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_REQUESTED", target_type="DEVICE", target_id=host.id))
            enqueue_realtime_event(
                session,
                "PAIRING_REQUESTED",
                host.id,
                host_id=host.id,
                client_id=device.id,
                pairing_id=pairing.id,
                payload={"pairingId": pairing.id, "clientId": device.id, "clientName": device.name, "status": pairing.status},
            )
            session.commit()
            return pairing_response(pairing)

    @app.get("/api/v1/pairings/pending", response_model=list[PendingPairing], tags=["Pairing"])
    def pending_pairings(device: Device = Depends(require_device)):
        require_role(device, "HOST")
        now = utcnow()
        with session_factory() as session:
            rows = session.execute(
                select(Pairing, Device.name)
                .join(Device, Device.id == Pairing.client_id)
                .where(Pairing.host_id == device.id, Pairing.status == "PENDING", Pairing.expires_at > now)
                .order_by(Pairing.created_at)
            ).all()
            return [PendingPairing(pairingId=pairing.id, clientId=pairing.client_id, clientName=name, code=pairing.code, expiresAt=pairing.expires_at, locationStatus=pairing.location_status) for pairing, name in rows]

    @app.get("/api/v1/pairings/summary", response_model=PairingSummary, tags=["Pairing"])
    def pairing_summary(device: Device = Depends(require_device)):
        require_role(device, "HOST")
        cutoff = utcnow() - timedelta(seconds=settings.heartbeat_timeout_seconds)
        with session_factory() as session:
            client_ids = set(session.scalars(
                select(Pairing.client_id)
                .join(Device, Device.id == Pairing.client_id)
                .where(Pairing.host_id == device.id, Pairing.status == "APPROVED", Device.is_revoked.is_(False))
            ).all())
            online_count = session.scalar(
                select(func.count(Device.id)).where(Device.id.in_(client_ids), Device.last_seen_at >= cutoff)
            ) if client_ids else 0
            return PairingSummary(hostId=device.id, approvedCount=len(client_ids), onlineCount=online_count or 0, maxClients=settings.max_clients_per_host)

    @app.post("/api/v1/pairings/{pairing_id}/decision", response_model=PairingResponse, tags=["Pairing"])
    def decide_pairing(pairing_id: str, payload: PairingDecision, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        now = utcnow()
        with session_factory() as session:
            pairing = session.scalar(select(Pairing).where(Pairing.id == pairing_id).with_for_update())
            if pairing is None or pairing.host_id != device.id:
                raise HTTPException(status_code=404, detail="Pairing not found")
            # Serialize approvals per Host so concurrent transactions cannot both consume the last slot.
            locked_host = session.scalar(select(Device).where(Device.id == device.id).with_for_update())
            if locked_host is None or locked_host.is_revoked:
                raise HTTPException(status_code=401, detail="Host is not active")
            if pairing.status != "PENDING" or as_utc(pairing.expires_at) <= now:
                raise HTTPException(status_code=409, detail="Pairing is no longer pending")
            client = session.get(Device, pairing.client_id)
            if client is None or client.is_revoked or client.role != "CLIENT":
                raise HTTPException(status_code=409, detail="Client is no longer active")
            if payload.approve:
                access_mode = payload.printerAccessMode or ("SELECTED_ONLY" if payload.allowedPrinters else "ALL_SHARED")
                if access_mode == "SELECTED_ONLY" and not payload.allowedPrinters:
                    access_mode = "NONE"
                approved_client_ids = set(session.scalars(select(Pairing.client_id).where(Pairing.host_id == device.id, Pairing.status == "APPROVED")).all())
                if pairing.client_id not in approved_client_ids and len(approved_client_ids) >= settings.max_clients_per_host:
                    raise HTTPException(status_code=409, detail=f"Host đã đủ {settings.max_clients_per_host} Client được phê duyệt")
                if access_mode == "SELECTED_ONLY" and payload.allowedPrinters:
                    selected_ids = set(payload.allowedPrinters)
                    owned_shared = set(session.scalars(select(Printer.id).where(
                        Printer.host_id == device.id,
                        Printer.is_shared.is_(True),
                        Printer.id.in_(selected_ids),
                    )).all())
                    if owned_shared != selected_ids:
                        raise HTTPException(status_code=409, detail="Selected printers must belong to this Host and be shared")
                elif access_mode != "SELECTED_ONLY" and payload.allowedPrinters:
                    raise HTTPException(status_code=422, detail="Only SELECTED_ONLY may contain selected printer IDs")
            else:
                access_mode = "NONE"
            pairing.status = "APPROVED" if payload.approve else "REJECTED"
            pairing.allowed_printers = payload.allowedPrinters if payload.approve and access_mode == "SELECTED_ONLY" else []
            pairing.printer_access_mode = access_mode
            pairing.decided_at = now
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_APPROVED" if payload.approve else "PAIRING_REJECTED", target_type="PAIRING", target_id=pairing.id))
            enqueue_realtime_event(
                session,
                "PAIRING_APPROVED" if payload.approve else "PAIRING_REJECTED",
                pairing.client_id,
                host_id=pairing.host_id,
                client_id=pairing.client_id,
                pairing_id=pairing.id,
                payload={"pairingId": pairing.id, "status": pairing.status, "printerAccessMode": pairing.printer_access_mode, "allowedPrinters": pairing.allowed_printers or []},
            )
            session.commit()
            return pairing_response(pairing)

    @app.put("/api/v1/pairings/{pairing_id}/printer-permissions", response_model=PairingResponse, tags=["Pairing"])
    def update_pairing_printer_permissions(
        pairing_id: str,
        payload: PairingPrinterPermissionUpdate,
        device: Device = Depends(require_device),
    ):
        require_role(device, "HOST")
        now = utcnow()
        with session_factory() as session:
            pairing = session.scalar(select(Pairing).where(Pairing.id == pairing_id).with_for_update())
            if pairing is None or pairing.host_id != device.id:
                raise HTTPException(status_code=404, detail="Pairing not found")
            if pairing.status != "APPROVED":
                raise HTTPException(status_code=409, detail="Only an approved pairing can change printer permissions")

            access_mode = payload.printerAccessMode
            printer_ids = set(payload.allowedPrinters)
            if access_mode == "SELECTED_ONLY" and not printer_ids:
                access_mode = "NONE"
            if access_mode == "SELECTED_ONLY" and printer_ids:
                owned_shared = set(session.scalars(select(Printer.id).where(
                    Printer.host_id == device.id,
                    Printer.is_shared.is_(True),
                    Printer.id.in_(printer_ids),
                )).all())
                if owned_shared != printer_ids:
                    raise HTTPException(status_code=409, detail="Selected printers must belong to this Host and be shared")
            elif access_mode != "SELECTED_ONLY" and printer_ids:
                raise HTTPException(status_code=422, detail="Only SELECTED_ONLY may contain selected printer IDs")

            pairing.allowed_printers = sorted(printer_ids)
            pairing.printer_access_mode = access_mode
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_PRINTER_PERMISSIONS_UPDATED", target_type="PAIRING", target_id=pairing.id, details={
                "printerAccessMode": pairing.printer_access_mode,
                "allowedPrinters": pairing.allowed_printers,
            }))
            enqueue_realtime_event(
                session,
                "PAIRING_PRINTER_PERMISSIONS_UPDATED",
                pairing.client_id,
                host_id=pairing.host_id,
                client_id=pairing.client_id,
                pairing_id=pairing.id,
                payload={"pairingId": pairing.id, "printerAccessMode": pairing.printer_access_mode, "allowedPrinters": pairing.allowed_printers},
            )
            session.commit()
            return pairing_response(pairing)

    @app.get("/api/v1/pairings/{pairing_id}", response_model=PairingResponse, tags=["Pairing"])
    def get_pairing_status(pairing_id: str, device: Device = Depends(require_device)):
        with session_factory() as session:
            pairing = session.get(Pairing, pairing_id)
            if pairing is None or device.id not in {pairing.host_id, pairing.client_id}:
                raise HTTPException(status_code=404, detail="Pairing not found")
            return pairing_response(pairing)

    @app.get("/api/v1/pairings/approved/{client_id}", response_model=PairingResponse, tags=["Pairing"])
    def get_approved_pairing_for_client(client_id: str, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        with session_factory() as session:
            pairing = session.scalar(select(Pairing).where(
                Pairing.host_id == device.id,
                Pairing.client_id == client_id,
                Pairing.status == "APPROVED",
            ))
            if pairing is None:
                raise HTTPException(status_code=404, detail="Approved pairing not found")
            return pairing_response(pairing)

    @app.post("/api/v1/pairings/{pairing_id}/revoke", response_model=PairingResponse, tags=["Pairing"])
    def revoke_pairing(pairing_id: str, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        with session_factory() as session:
            pairing = session.scalar(select(Pairing).where(Pairing.id == pairing_id).with_for_update())
            if pairing is None or pairing.host_id != device.id:
                raise HTTPException(status_code=404, detail="Pairing not found")
            if pairing.status == "REVOKED":
                return pairing_response(pairing)
            pairing.status = "REVOKED"
            pairing.decided_at = utcnow()
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_REVOKED", target_type="PAIRING", target_id=pairing.id))
            enqueue_realtime_event(
                session,
                "PAIRING_REVOKED",
                pairing.client_id,
                host_id=pairing.host_id,
                client_id=pairing.client_id,
                pairing_id=pairing.id,
                payload={"pairingId": pairing.id, "status": pairing.status},
            )
            session.commit()
            return pairing_response(pairing)

    @app.get("/api/v1/devices/geofence", response_model=GeofenceResponse, tags=["Devices"])
    def get_geofence(device: Device = Depends(require_device)):
        require_role(device, "HOST")
        with session_factory() as session:
            activation = session.get(ActivationCode, device.activation_code_id) if device.activation_code_id else None
        policy = activation.geofence_policy if activation else (GEOFENCE_POLICY_LIMITED if device.geofence_enabled else "OPTIONAL")
        return GeofenceResponse(
            hostId=device.id,
            enabled=False if policy == GEOFENCE_POLICY_UNLIMITED else device.geofence_enabled,
            latitude=device.geofence_latitude,
            longitude=device.geofence_longitude,
            radiusMeters=device.geofence_radius_meters or 1000,
            policy=policy,
            maxRadiusMeters=activation.geofence_radius_meters if activation and activation.geofence_policy == GEOFENCE_POLICY_LIMITED else None,
        )

    @app.get("/api/v1/devices/{device_id}", response_model=DeviceProfile, tags=["Devices"])
    def get_authorized_device_profile(device_id: str, device: Device = Depends(require_device)):
        with session_factory() as session:
            target = session.get(Device, device_id)
            if target is None or target.is_revoked:
                raise HTTPException(status_code=404, detail="Device not found")
            if target.id != device.id:
                approved = session.scalar(
                    select(Pairing).where(
                        Pairing.status == "APPROVED",
                        ((Pairing.host_id == target.id) & (Pairing.client_id == device.id))
                        | ((Pairing.client_id == target.id) & (Pairing.host_id == device.id)),
                    )
                )
                if approved is None:
                    raise HTTPException(status_code=403, detail="Approved pairing required")
            activation = session.get(ActivationCode, target.activation_code_id) if target.activation_code_id else None
            return DeviceProfile(
                deviceId=target.id,
                name=target.name,
                role=target.role,
                appVersion=target.app_version,
                isRevoked=target.is_revoked,
                lastSeenAt=target.last_seen_at,
                publicKey=target.public_key,
                keyAlgorithm=target.key_algorithm,
                activationLabel=activation.label if activation else "Quản trị viên",
                activationExpiresAt=activation.expires_at if activation else None,
                activationMaxDevices=activation.max_devices if activation else None,
                activationUsedDevices=activation.used_devices if activation else None,
                activationGeofencePolicy=activation.geofence_policy if activation else None,
                activationGeofenceRadiusMeters=activation.geofence_radius_meters if activation else None,
            )

    @app.put("/api/v1/devices/geofence", response_model=GeofenceResponse, tags=["Devices"])
    def update_geofence(payload: GeofenceSettings, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        if payload.enabled and (payload.latitude is None or payload.longitude is None or payload.radiusMeters is None):
            raise HTTPException(status_code=422, detail="Latitude and longitude are required when geofence is enabled")
        with session_factory() as session:
            host = session.get(Device, device.id)
            activation = session.get(ActivationCode, host.activation_code_id) if host.activation_code_id else None
            if activation is not None and payload.policy != activation.geofence_policy:
                raise HTTPException(status_code=403, detail="Geofence policy is controlled by the activation code")
            if activation is not None and activation.geofence_policy == GEOFENCE_POLICY_UNLIMITED:
                if payload.enabled:
                    raise HTTPException(status_code=403, detail="Geofence is not used by an unlimited activation policy")
                host.geofence_enabled = False
                host.geofence_latitude = None
                host.geofence_longitude = None
                host.geofence_radius_meters = 1000
                session.add(AuditEvent(actor_id=device.id, action="GEOFENCE_POLICY_CONFIRMED_UNLIMITED", target_type="DEVICE", target_id=device.id))
                session.commit()
                return GeofenceResponse(hostId=host.id, enabled=False, latitude=None, longitude=None, radiusMeters=1000, policy=GEOFENCE_POLICY_UNLIMITED, maxRadiusMeters=None)
            if activation is not None and not payload.enabled:
                raise HTTPException(status_code=403, detail="Geofence is required by the activation code")
            if activation is not None and activation.geofence_policy == GEOFENCE_POLICY_LIMITED and (
                activation.geofence_radius_meters is None or payload.radiusMeters > activation.geofence_radius_meters
            ):
                raise HTTPException(
                    status_code=422,
                    detail=f"Geofence radius cannot exceed the activation limit of {activation.geofence_radius_meters or 0} meters",
                )
            host.geofence_enabled = payload.enabled
            host.geofence_latitude = payload.latitude
            host.geofence_longitude = payload.longitude
            host.geofence_radius_meters = payload.radiusMeters
            session.add(AuditEvent(actor_id=device.id, action="GEOFENCE_UPDATED", target_type="DEVICE", target_id=device.id))
            session.commit()
            return GeofenceResponse(
                hostId=host.id,
                enabled=host.geofence_enabled,
                latitude=host.geofence_latitude,
                longitude=host.geofence_longitude,
                radiusMeters=host.geofence_radius_meters,
                policy=activation.geofence_policy if activation else GEOFENCE_POLICY_LIMITED,
                maxRadiusMeters=activation.geofence_radius_meters if activation and activation.geofence_policy == GEOFENCE_POLICY_LIMITED else None,
            )

    @app.post("/api/v1/pairing-codes/issue", response_model=PairingCodeResponse, status_code=201, tags=["Pairing codes"])
    def issue_code(payload: PairingCodeIssueRequest, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        now = utcnow()
        with session_factory() as session:
            existing = session.scalar(select(PairingCode).where(PairingCode.session_id == payload.sessionId))
            if existing:
                if existing.device_id != device.id:
                    raise HTTPException(status_code=409, detail="Session belongs to another device")
                if existing.status == "ACTIVE" and as_utc(existing.expires_at) <= now:
                    existing.status = "EXPIRED"
                    session.commit()
                code = decrypt_pairing_code(existing.code_ciphertext, settings.enrollment_secret) if existing.status == "ACTIVE" else None
                return PairingCodeResponse(
                    sessionId=existing.session_id, code=code, issuedAt=existing.issued_at,
                    expiresAt=existing.expires_at, status=existing.status,
                )
            active_codes = session.scalars(select(PairingCode).where(
                PairingCode.device_id == device.id, PairingCode.status == "ACTIVE"
            )).all()
            for active_code in active_codes:
                active_code.status = "REVOKED"
                active_code.revoked_at = now
                _revoke_pairings_for_code(
                    session,
                    device.id,
                    decrypt_pairing_code(active_code.code_ciphertext, settings.enrollment_secret),
                    now,
                    enqueue_realtime_event,
                )
            expires_at = now + timedelta(minutes=settings.pairing_ttl_minutes)
            try:
                record, plain_code = issue_pairing_code(
                    session, device.id, payload.sessionId, expires_at, settings.enrollment_secret
                )
            except ValueError as exc:
                raise HTTPException(status_code=409, detail=str(exc))
            except RuntimeError:
                session.rollback()
                raise HTTPException(status_code=503, detail="No unique pairing code is currently available")
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_CODE_ISSUED", target_type="PAIRING_CODE", target_id=record.id))
            session.commit()
            return PairingCodeResponse(
                sessionId=record.session_id, code=plain_code, issuedAt=record.issued_at,
                expiresAt=record.expires_at, status=record.status,
            )

    @app.get("/api/v1/pairing-codes/current", response_model=PairingCodeResponse, tags=["Pairing codes"])
    def current_code(device: Device = Depends(require_device)):
        require_role(device, "HOST")
        now = utcnow()
        with session_factory() as session:
            record = session.scalar(
                select(PairingCode)
                .where(PairingCode.device_id == device.id)
                # A host keeps revoked/expired rows as audit history. The
                # current endpoint must prefer the one live code rather than
                # relying on timestamp ordering when SQLite truncates ties.
                .order_by(
                    (PairingCode.status == "ACTIVE").desc(),
                    PairingCode.issued_at.desc(),
                    PairingCode.id.desc(),
                )
            )
            if record is None:
                raise HTTPException(status_code=404, detail="No pairing code has been issued")
            if record.status == "ACTIVE" and as_utc(record.expires_at) <= now:
                record.status = "EXPIRED"
                session.commit()
            code = decrypt_pairing_code(record.code_ciphertext, settings.enrollment_secret) if record.status == "ACTIVE" else None
            return PairingCodeResponse(
                sessionId=record.session_id, code=code, issuedAt=record.issued_at,
                expiresAt=record.expires_at, status=record.status,
            )

    @app.post("/api/v1/pairing-codes/resolve", response_model=PairingCodeResolveResponse, status_code=201, tags=["Pairing codes"])
    def resolve_code(payload: PairingCodeResolveRequest, request: Request, device: Device = Depends(require_device)):
        require_role(device, "CLIENT")
        with session_factory() as session:
            enforce_rate_limit(
                session,
                f"pairing-resolve:{device.id}",
                settings.rate_limit_max_attempts,
                settings.rate_limit_window_seconds,
            )
            record = session.scalar(select(PairingCode).where(PairingCode.code_hash == hash_pairing_code(payload.code)))
            now = utcnow()
            if record is None or record.status != "ACTIVE" or as_utc(record.expires_at) <= now:
                if record is not None and record.status == "ACTIVE":
                    record.status = "EXPIRED"
                session.add(AuditEvent(actor_id=device.id, action="PAIRING_CODE_REJECTED", target_type="PAIRING_CODE", target_id=record.id if record else None))
                session.commit()
                raise HTTPException(status_code=404, detail="Invalid or expired pairing code")
            host = session.get(Device, record.device_id)
            if host is None or host.role != "HOST" or host.is_revoked or host.id == device.id:
                raise HTTPException(status_code=404, detail="Invalid or expired pairing code")

            # Repeated clicks/retries must not create multiple live requests for
            # the same Host/Client pair. The existing row remains authoritative.
            _close_expired_live_pairings(session, host.id, device.id, now, enqueue_realtime_event)
            existing = session.scalar(
                select(Pairing)
                .where(
                    Pairing.host_id == host.id,
                    Pairing.client_id == device.id,
                    (Pairing.status == "APPROVED")
                    | ((Pairing.status == "PENDING") & (Pairing.expires_at > now)),
                )
                .order_by(Pairing.created_at.desc())
            )
            if existing is not None:
                return PairingCodeResolveResponse(
                    pairingId=existing.id,
                    hostId=existing.host_id,
                    clientId=existing.client_id,
                    status=existing.status,
                    locationStatus=existing.location_status,
                )

            activation = session.get(ActivationCode, host.activation_code_id) if host.activation_code_id else None
            location_result = verify_pairing_location(host, activation, location_payload(host.id, payload), utcnow())
            pairing = Pairing(
                host_id=host.id, client_id=device.id, code=payload.code,
                status="PENDING", expires_at=record.expires_at,
            )
            apply_location_evidence(pairing, payload, location_result)
            try:
                with session.begin_nested():
                    session.add(pairing)
                    session.flush()
            except IntegrityError:
                existing = session.scalar(
                    select(Pairing)
                    .where(
                        Pairing.host_id == host.id,
                        Pairing.client_id == device.id,
                        (Pairing.status == "APPROVED")
                        | ((Pairing.status == "PENDING") & (Pairing.expires_at > now)),
                    )
                    .order_by(Pairing.created_at.desc())
                )
                if existing is None:
                    raise
                return PairingCodeResolveResponse(
                    pairingId=existing.id,
                    hostId=existing.host_id,
                    clientId=existing.client_id,
                    status=existing.status,
                    locationStatus=existing.location_status,
                )
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_REQUESTED", target_type="DEVICE", target_id=host.id))
            enqueue_realtime_event(
                session,
                "PAIRING_REQUESTED",
                host.id,
                host_id=host.id,
                client_id=device.id,
                pairing_id=pairing.id,
                payload={"pairingId": pairing.id, "clientId": device.id, "clientName": device.name, "status": pairing.status},
            )
            session.commit()
            return PairingCodeResolveResponse(
                pairingId=pairing.id,
                hostId=host.id,
                clientId=device.id,
                status=pairing.status,
                locationStatus=pairing.location_status,
            )

    @app.post("/api/v1/pairing-codes/revoke", response_model=PairingCodeResponse, tags=["Pairing codes"])
    def revoke_code(payload: PairingCodeIssueRequest, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        now = utcnow()
        with session_factory() as session:
            record = session.scalar(select(PairingCode).where(PairingCode.session_id == payload.sessionId, PairingCode.device_id == device.id))
            if record is None:
                raise HTTPException(status_code=404, detail="Pairing code not found")
            if record.status == "ACTIVE":
                record.status = "REVOKED"
                record.revoked_at = now
                _revoke_pairings_for_code(
                    session,
                    device.id,
                    decrypt_pairing_code(record.code_ciphertext, settings.enrollment_secret),
                    now,
                    enqueue_realtime_event,
                )
                session.add(AuditEvent(actor_id=device.id, action="PAIRING_CODE_REVOKED", target_type="PAIRING_CODE", target_id=record.id))
                session.commit()
            return PairingCodeResponse(
                sessionId=record.session_id, code=None, issuedAt=record.issued_at,
                expiresAt=record.expires_at, status=record.status,
            )

    @app.post("/api/v1/printers", response_model=PrinterResponse, tags=["Printers"])
    def register_printer(payload: PrinterRegistration, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        with session_factory() as session:
            printer = session.get(Printer, payload.printerId)
            if printer is not None and printer.host_id != device.id:
                raise HTTPException(status_code=409, detail="Printer belongs to another Host")
            if printer is None:
                printer = Printer(id=payload.printerId, host_id=device.id, display_name=payload.displayName)
                session.add(printer)
            printer.display_name = payload.displayName
            printer.reported_status = payload.reportedStatus
            printer.is_shared = payload.isShared
            printer.metadata_json = payload.metadata
            approved_pairings = session.scalars(select(Pairing).where(
                Pairing.host_id == device.id,
                Pairing.status == "APPROVED",
            )).all()
            for pairing in approved_pairings:
                enqueue_realtime_event(
                    session,
                    "PRINTER_CATALOG_UPDATED",
                    pairing.client_id,
                    host_id=pairing.host_id,
                    client_id=pairing.client_id,
                    pairing_id=pairing.id,
                    payload={"printerId": printer.id, "isShared": printer.is_shared},
                )
            session.commit()
            return PrinterResponse(
                printerId=printer.id, hostId=printer.host_id, displayName=printer.display_name,
                reportedStatus=printer.reported_status, isShared=printer.is_shared, metadata=printer.metadata_json,
            )

    @app.get("/api/v1/printers", response_model=list[PrinterResponse], tags=["Printers"])
    def list_printers(hostId: str, device: Device = Depends(require_device)):
        with session_factory() as session:
            host = session.get(Device, hostId)
            if host is None or host.role != "HOST" or host.is_revoked:
                raise HTTPException(status_code=404, detail="Host not found")
            if device.id != hostId:
                pairing = session.scalar(
                    select(Pairing).where(
                        Pairing.host_id == hostId,
                        Pairing.client_id == device.id,
                        Pairing.status == "APPROVED",
                    )
                )
                if pairing is None:
                    raise HTTPException(status_code=403, detail="Approved pairing required")
                printers = session.scalars(select(Printer).where(Printer.host_id == hostId, Printer.is_shared.is_(True))).all()
                if pairing.printer_access_mode == "NONE":
                    printers = []
                elif pairing.printer_access_mode == "SELECTED_ONLY" or pairing.allowed_printers:
                    printers = [item for item in printers if item.id in (pairing.allowed_printers or [])]
            else:
                require_role(device, "HOST")
                printers = session.scalars(select(Printer).where(Printer.host_id == hostId)).all()
            return [
                PrinterResponse(
                    printerId=item.id, hostId=item.host_id, displayName=item.display_name,
                    reportedStatus=item.reported_status, isShared=item.is_shared, metadata=item.metadata_json,
                )
                for item in printers
            ]

    @app.post("/api/v1/print-authorizations", response_model=PrintAuthorizationResponse, status_code=201, tags=["Print authorizations"])
    def create_print_authorization(payload: PrintAuthorizationRequest, device: Device = Depends(require_device)):
        require_role(device, "CLIENT")
        now = utcnow()
        with session_factory() as session:
            _, _, host, activation = require_approved_printer_access(session, device, payload.hostId, payload.printerId)
            verify_pairing_location(host, activation, location_payload(host.id, payload), now)
            # A grant is single-use. Reissuing the same local job id invalidates
            # any previous grant so a retry cannot replay an older transport token.
            previous = session.scalars(select(PrintAuthorization).where(
                PrintAuthorization.job_id == payload.jobId,
                PrintAuthorization.client_id == device.id,
                PrintAuthorization.host_id == host.id,
                PrintAuthorization.used_at.is_(None),
            )).all()
            for item in previous:
                item.used_at = now
            token = secrets.token_urlsafe(32)
            authorization = PrintAuthorization(
                token_hash=hash_token(token),
                job_id=payload.jobId,
                client_id=device.id,
                host_id=host.id,
                printer_id=payload.printerId,
                expires_at=now + timedelta(seconds=90),
            )
            session.add(authorization)
            session.add(AuditEvent(actor_id=device.id, action="PRINT_AUTHORIZATION_ISSUED", target_type="PRINT_AUTHORIZATION", target_id=authorization.id, details={"jobId": payload.jobId, "geofencePolicy": activation.geofence_policy if activation else "OPTIONAL"}))
            session.commit()
            return PrintAuthorizationResponse(
                authorizationId=authorization.id,
                authorizationToken=token,
                jobId=authorization.job_id,
                printerId=authorization.printer_id,
                expiresAt=authorization.expires_at,
            )

    @app.post("/api/v1/print-authorizations/{authorization_id}/consume", response_model=PrintAuthorizationConsumeResponse, tags=["Print authorizations"])
    def consume_print_authorization(
        authorization_id: str,
        payload: PrintAuthorizationConsumeRequest,
        device: Device = Depends(require_device),
    ):
        require_role(device, "HOST")
        now = utcnow()
        with session_factory() as session:
            authorization = session.scalar(select(PrintAuthorization).where(PrintAuthorization.id == authorization_id).with_for_update())
            if authorization is None or authorization.host_id != device.id:
                raise HTTPException(status_code=404, detail="Print authorization not found")
            if authorization.used_at is not None or as_utc(authorization.expires_at) <= now:
                raise HTTPException(status_code=409, detail="Print authorization is expired or already used")
            if not secrets.compare_digest(authorization.token_hash, hash_token(payload.authorizationToken)):
                raise HTTPException(status_code=403, detail="Invalid print authorization")
            pairing = session.scalar(select(Pairing).where(
                Pairing.host_id == authorization.host_id,
                Pairing.client_id == authorization.client_id,
                Pairing.status == "APPROVED",
            ))
            printer = session.get(Printer, authorization.printer_id)
            client = session.get(Device, authorization.client_id)
            if pairing is None or printer is None or client is None or client.is_revoked or not printer.is_shared:
                raise HTTPException(status_code=403, detail="Print permission is no longer valid")
            if not pairing_allows_printer(pairing, printer.id):
                raise HTTPException(status_code=403, detail="Printer is not allowed for this pairing")
            authorization.used_at = now
            session.add(AuditEvent(actor_id=device.id, action="PRINT_AUTHORIZATION_CONSUMED", target_type="PRINT_AUTHORIZATION", target_id=authorization.id, details={"jobId": authorization.job_id}))
            session.commit()
            return PrintAuthorizationConsumeResponse(
                authorized=True,
                jobId=authorization.job_id,
                clientId=authorization.client_id,
                hostId=authorization.host_id,
                printerId=authorization.printer_id,
            )

    @app.post("/api/v1/jobs", response_model=PrintJobResponse, status_code=201, tags=["Print jobs"])
    def create_print_job(
        payload: PrintJobRequest,
        idempotency_key: str | None = Header(default=None, alias="Idempotency-Key"),
        device: Device = Depends(require_device),
    ):
        require_role(device, "CLIENT")
        if not idempotency_key or len(idempotency_key) > 200:
            raise HTTPException(status_code=400, detail="Idempotency-Key is required")
        request_hash = hashlib.sha256(
            json.dumps(payload.model_dump(), sort_keys=True, separators=(",", ":")).encode("utf-8")
        ).hexdigest()
        with session_factory() as session:
            existing = session.scalar(select(PrintJob).where(PrintJob.idempotency_key == idempotency_key))
            if existing:
                if existing.request_hash != request_hash or existing.client_id != device.id:
                    raise HTTPException(status_code=409, detail="Idempotency-Key was reused with another request")
                return PrintJobResponse(
                    jobId=existing.id, idempotencyKey=existing.idempotency_key, clientId=existing.client_id,
                    hostId=existing.host_id, printerId=existing.printer_id, status=existing.status, metadata=existing.metadata_json,
                )
            tombstone = session.scalar(select(PrintJobTombstone).where(PrintJobTombstone.idempotency_key == idempotency_key))
            if tombstone is not None:
                raise HTTPException(status_code=409, detail="Idempotency-Key belongs to an expired print job")
            pairing = session.scalar(select(Pairing).where(Pairing.host_id == payload.hostId, Pairing.client_id == device.id, Pairing.status == "APPROVED"))
            printer = session.get(Printer, payload.printerId)
            if pairing is None or printer is None or printer.host_id != payload.hostId or not printer.is_shared:
                raise HTTPException(status_code=403, detail="Approved shared printer access required")
            if not pairing_allows_printer(pairing, printer.id):
                raise HTTPException(status_code=403, detail="Printer is not allowed for this pairing")
            host = session.get(Device, payload.hostId)
            if host is None:
                raise HTTPException(status_code=404, detail="Host not found")
            activation = session.get(ActivationCode, host.activation_code_id) if host.activation_code_id else None
            verify_pairing_location(host, activation, location_payload(host.id, payload), utcnow())
            job = PrintJob(
                idempotency_key=idempotency_key, client_id=device.id, host_id=payload.hostId,
                printer_id=payload.printerId, request_hash=request_hash, metadata_json=payload.metadata,
                data_ref=payload.dataRef, content_sha256=payload.contentSha256,
                content_size=payload.contentSize, content_type=payload.contentType,
            )
            try:
                with session.begin_nested():
                    session.add(job)
                    session.flush()
            except IntegrityError:
                existing = session.scalar(select(PrintJob).where(PrintJob.idempotency_key == idempotency_key))
                if existing is None or existing.request_hash != request_hash or existing.client_id != device.id:
                    raise HTTPException(status_code=409, detail="Idempotency-Key was reused with another request")
                session.rollback()
                return job_response(existing)
            session.add(AuditEvent(actor_id=device.id, action="PRINT_JOB_CREATED", target_type="PRINT_JOB", target_id=job.id))
            session.flush()
            enqueue_realtime_event(
                session,
                "PRINT_JOB_CREATED",
                job.host_id,
                host_id=job.host_id,
                client_id=job.client_id,
                job_id=job.id,
                payload={"jobId": job.id, "clientId": job.client_id, "hostId": job.host_id, "printerId": job.printer_id, "status": job.status},
            )
            session.commit()
            return job_response(job)

    @app.get("/api/v1/jobs", response_model=list[PrintJobResponse], tags=["Print jobs"])
    def list_host_jobs(status: str = "QUEUED", limit: int = 50, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        if limit < 1 or limit > 100:
            raise HTTPException(status_code=422, detail="limit must be between 1 and 100")
        with session_factory() as session:
            query = select(PrintJob).where(PrintJob.host_id == device.id)
            allowed = {"QUEUED", "CLAIMED", "DOWNLOADING", "SPOOLING", "SPOOLER_ACCEPTED", "COMPLETED", "FAILED", "CANCELED", "UNKNOWN"}
            if status not in allowed:
                raise HTTPException(status_code=422, detail="Invalid job status")
            query = query.where(PrintJob.status == status)
            jobs = session.scalars(query.order_by(PrintJob.created_at).limit(limit)).all()
            return [job_response(job) for job in jobs]

    @app.get("/api/v1/jobs/{job_id}", response_model=PrintJobResponse, tags=["Print jobs"])
    def get_print_job(job_id: str, device: Device = Depends(require_device)):
        with session_factory() as session:
            return job_response(require_job_access(session, job_id, device))

    @app.put("/api/v1/jobs/{job_id}/document", response_model=PrintJobResponse, tags=["Print jobs"])
    async def upload_print_document(
        job_id: str,
        document: UploadFile = File(...),
        device: Device = Depends(require_device),
    ):
        require_role(device, "CLIENT")
        if app.state.blob_store is None:
            raise HTTPException(status_code=503, detail="Document storage is not configured")
        with session_factory() as session:
            job = require_job_access(session, job_id, device)
            if job.client_id != device.id or job.status != "QUEUED":
                raise HTTPException(status_code=409, detail="Job is not accepting a document")
            if job.data_ref:
                raise HTTPException(status_code=409, detail="Document is already uploaded")
            chunks: list[bytes] = []
            total = 0
            digest = hashlib.sha256()
            while True:
                chunk = await document.read(64 * 1024)
                if not chunk:
                    break
                total += len(chunk)
                if total > settings.max_document_bytes:
                    raise HTTPException(status_code=413, detail="Document is too large")
                digest.update(chunk)
                chunks.append(chunk)
            encrypted_bytes = b"".join(chunks)
            encrypted_hash = digest.hexdigest()
            if job.content_sha256 and job.content_sha256.lower() != encrypted_hash:
                raise HTTPException(status_code=422, detail="Document integrity hash does not match the job")
            if job.content_size is not None and job.content_size != total:
                raise HTTPException(status_code=422, detail="Document size does not match the job")
            # The upload is ciphertext; never let a client choose an executable or HTML response type.
            content_type = "application/octet-stream"
            reference = await asyncio.to_thread(
                app.state.blob_store.put,
                f"jobs/{job.id}/{secrets.token_hex(16)}.bin",
                encrypted_bytes,
                content_type,
            )
            job.data_ref = reference
            job.content_sha256 = encrypted_hash
            job.content_size = total
            job.content_type = content_type
            job.updated_at = utcnow()
            session.add(AuditEvent(actor_id=device.id, action="PRINT_DOCUMENT_UPLOADED", target_type="PRINT_JOB", target_id=job.id))
            session.commit()
            return job_response(job)

    @app.get("/api/v1/jobs/{job_id}/document", tags=["Print jobs"])
    def download_print_document(
        job_id: str,
        claim_token: str | None = Header(default=None, alias="X-Claim-Token"),
        device: Device = Depends(require_device),
    ):
        if app.state.blob_store is None:
            raise HTTPException(status_code=503, detail="Document storage is not configured")
        if not claim_token:
            raise HTTPException(status_code=401, detail="Claim token required")
        with session_factory() as session:
            job = require_job_access(session, job_id, device)
            require_claim(session, job, device, claim_token)
            if not job.data_ref:
                raise HTTPException(status_code=404, detail="Document is not uploaded")
            try:
                encrypted_bytes = app.state.blob_store.get(job.data_ref)
            except Exception:
                raise HTTPException(status_code=502, detail="Document storage unavailable")
            if len(encrypted_bytes) != job.content_size or hashlib.sha256(encrypted_bytes).hexdigest() != job.content_sha256:
                raise HTTPException(status_code=502, detail="Document integrity check failed")
            return Response(
                content=encrypted_bytes,
                media_type=job.content_type or "application/octet-stream",
                headers={"X-Content-SHA256": job.content_sha256, "X-Content-Length": str(job.content_size)},
            )

    @app.post("/api/v1/jobs/{job_id}/claim", response_model=JobClaimResponse, tags=["Print jobs"])
    def claim_print_job(job_id: str, payload: JobClaimRequest, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        now = utcnow()
        with session_factory() as session:
            job = session.scalar(select(PrintJob).where(PrintJob.id == job_id, PrintJob.host_id == device.id).with_for_update(skip_locked=True))
            if job is None:
                raise HTTPException(status_code=404, detail="Print job not found")
            if job.status == "CLAIMED" and job.claim_expires_at and as_utc(job.claim_expires_at) <= now:
                job.status = "QUEUED"
                job.claim_token_hash = None
                job.claim_expires_at = None
            if job.status != "QUEUED":
                raise HTTPException(status_code=409, detail="Print job is not claimable")
            claim_token = secrets.token_urlsafe(32)
            job.status = "CLAIMED"
            job.claimed_at = now
            job.claim_token_hash = hash_token(claim_token)
            job.claim_expires_at = now + timedelta(seconds=payload.leaseSeconds)
            job.updated_at = now
            session.add(AuditEvent(actor_id=device.id, action="PRINT_JOB_CLAIMED", target_type="PRINT_JOB", target_id=job.id))
            session.commit()
            return job_response(job, claim_token)

    @app.post("/api/v1/jobs/{job_id}/renew", response_model=PrintJobResponse, tags=["Print jobs"])
    def renew_print_job(job_id: str, payload: JobRenewRequest, device: Device = Depends(require_device)):
        with session_factory() as session:
            job = require_job_access(session, job_id, device)
            require_claim(session, job, device, payload.claimToken)
            if job.status not in {"CLAIMED", "DOWNLOADING", "SPOOLING"}:
                raise HTTPException(status_code=409, detail="Job is no longer active")
            job.claim_expires_at = utcnow() + timedelta(seconds=payload.leaseSeconds)
            job.updated_at = utcnow()
            session.commit()
            return job_response(job)

    @app.post("/api/v1/jobs/{job_id}/ack", response_model=PrintJobResponse, tags=["Print jobs"])
    def acknowledge_print_job(job_id: str, payload: JobAcknowledgeRequest, device: Device = Depends(require_device)):
        with session_factory() as session:
            job = require_job_access(session, job_id, device)
            require_claim(session, job, device, payload.claimToken)
            if job.status != "CLAIMED":
                raise HTTPException(status_code=409, detail="Job is not awaiting receipt acknowledgement")
            job.status = "DOWNLOADING"
            job.updated_at = utcnow()
            session.add(AuditEvent(actor_id=device.id, action="PRINT_JOB_ACKNOWLEDGED", target_type="PRINT_JOB", target_id=job.id))
            session.commit()
            return job_response(job)

    @app.patch("/api/v1/jobs/{job_id}/status", response_model=PrintJobResponse, tags=["Print jobs"])
    def update_print_job_status(job_id: str, payload: JobStatusUpdateRequest, device: Device = Depends(require_device)):
        transitions = {
            "CLAIMED": {"DOWNLOADING", "FAILED", "UNKNOWN"},
            "DOWNLOADING": {"SPOOLING", "FAILED", "UNKNOWN"},
            "SPOOLING": {"SPOOLER_ACCEPTED", "FAILED", "UNKNOWN"},
            "SPOOLER_ACCEPTED": {"COMPLETED", "FAILED", "UNKNOWN"},
        }
        with session_factory() as session:
            job = require_job_access(session, job_id, device)
            require_claim(session, job, device, payload.claimToken)
            if payload.status not in transitions.get(job.status, set()):
                raise HTTPException(status_code=409, detail=f"Invalid status transition from {job.status}")
            now = utcnow()
            job.status = payload.status
            job.spooler_job_id = payload.spoolerJobId or job.spooler_job_id
            job.failure_code = payload.failureCode
            job.failure_message = payload.failureMessage
            job.completed_at = now if payload.status == "COMPLETED" else job.completed_at
            job.updated_at = now
            if payload.status in {"COMPLETED", "FAILED", "UNKNOWN"}:
                job.claim_expires_at = None
            session.add(AuditEvent(actor_id=device.id, action=f"PRINT_JOB_{payload.status}", target_type="PRINT_JOB", target_id=job.id))
            event_type = {
                "DOWNLOADING": "PRINT_JOB_TRANSFER_STARTED",
                "SPOOLING": "PRINT_JOB_SPOOLING",
                "SPOOLER_ACCEPTED": "PRINT_JOB_SPOOLER_ACCEPTED",
                "COMPLETED": "PRINT_JOB_COMPLETED",
                "FAILED": "PRINT_JOB_FAILED",
                "UNKNOWN": "PRINT_JOB_UNKNOWN",
            }[payload.status]
            enqueue_realtime_event(
                session,
                event_type,
                job.client_id,
                host_id=job.host_id,
                client_id=job.client_id,
                job_id=job.id,
                payload={
                    "jobId": job.id,
                    "status": job.status,
                    "spoolerJobId": job.spooler_job_id,
                    "failureCode": job.failure_code,
                    "failureMessage": job.failure_message,
                },
            )
            session.commit()
            return job_response(job)

    @app.post("/api/v1/jobs/{job_id}/cancel", response_model=PrintJobResponse, tags=["Print jobs"])
    def cancel_print_job(job_id: str, payload: JobCancelRequest, device: Device = Depends(require_device)):
        with session_factory() as session:
            job = require_job_access(session, job_id, device)
            if job.status not in {"QUEUED", "CLAIMED"}:
                raise HTTPException(status_code=409, detail="Job can no longer be canceled")
            job.status = "CANCELED"
            job.failure_message = payload.reason
            job.claim_expires_at = None
            job.updated_at = utcnow()
            session.add(AuditEvent(actor_id=device.id, action="PRINT_JOB_CANCELED", target_type="PRINT_JOB", target_id=job.id))
            enqueue_realtime_event(
                session,
                "PRINT_JOB_CANCELED",
                job.host_id,
                host_id=job.host_id,
                client_id=job.client_id,
                job_id=job.id,
                payload={"jobId": job.id, "status": job.status, "failureMessage": job.failure_message},
            )
            session.commit()
            return job_response(job)

    @app.get("/api/v1/internal/cleanup", tags=["Internal"])
    def internal_cleanup(dry_run: bool = False, authorization: str | None = Header(default=None)):
        if not settings.cron_secret or not authorization or not authorization.startswith("Bearer "):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Cron authorization required")
        if not secrets.compare_digest(authorization.removeprefix("Bearer ").strip(), settings.cron_secret):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid cron authorization")
        return run_cleanup(session_factory, engine, app.state.blob_store, dry_run=dry_run)

    @app.get("/api/v1/internal/realtime/events", response_model=list[RealtimeEventEnvelope], tags=["Internal"])
    def internal_realtime_events(
        after: int = 0,
        limit: int = 100,
        gateway_key: str | None = Header(default=None, alias="X-Realtime-Gateway-Key"),
    ):
        if not settings.realtime_gateway_shared_secret or not gateway_key or not secrets.compare_digest(gateway_key, settings.realtime_gateway_shared_secret):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Realtime gateway authorization required")
        if after < 0 or limit < 1 or limit > 500:
            raise HTTPException(status_code=422, detail="Invalid event cursor or limit")
        with session_factory() as session:
            events = session.scalars(select(RealtimeEvent).where(RealtimeEvent.id > after).order_by(RealtimeEvent.id).limit(limit)).all()
            return [event_envelope(event) for event in events]

    @app.get("/api/v1/internal/realtime/authorize/{device_id}", tags=["Internal"])
    def authorize_realtime_device(
        device_id: str,
        gateway_key: str | None = Header(default=None, alias="X-Realtime-Gateway-Key"),
    ):
        if not settings.realtime_gateway_shared_secret or not gateway_key or not secrets.compare_digest(gateway_key, settings.realtime_gateway_shared_secret):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Realtime gateway authorization required")
        with session_factory() as session:
            device = session.get(Device, device_id)
            if device is None or device.is_revoked:
                return {"authorized": False}
            if device.activation_code_id:
                activation = session.get(ActivationCode, device.activation_code_id)
                if activation is None or activation.revoked_at is not None or (
                    activation.expires_at is not None and as_utc(activation.expires_at) <= utcnow()
                ):
                    return {"authorized": False}
            return {"authorized": True, "role": device.role}

    @app.post("/api/v1/internal/telegram/webhook", tags=["Internal"])
    async def telegram_webhook(request: Request):
        webhook_secret = request.headers.get("X-Telegram-Bot-Api-Secret-Token", "")
        if (
            not settings.telegram_bot_token
            or not settings.telegram_webhook_secret
            or not secrets.compare_digest(webhook_secret, settings.telegram_webhook_secret)
        ):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Telegram webhook authorization required")

        update = await request.json()
        message = update.get("message") or {}
        sender = message.get("from") or {}
        chat = message.get("chat") or {}
        admin_id = int(sender.get("id", 0))
        chat_id = int(chat.get("id", 0))
        if admin_id not in settings.telegram_admin_ids or chat_id == 0:
            return {"ok": True}

        command = parse_command(str(message.get("text", "")))
        if command is None:
            return {"ok": True}

        incoming_message_id = int(message.get("message_id", 0))
        with session_factory() as session:
            record_message(session, chat_id, incoming_message_id, "IN")
            session.commit()

        if command.name == "/clear-chat":
            with session_factory() as session:
                message_ids = tracked_message_ids(session, chat_id)
            results = await asyncio.gather(*(
                asyncio.to_thread(delete_message, settings.telegram_bot_token, chat_id, message_id)
                for message_id in message_ids
            ))
            deleted_ids = [message_id for message_id, deleted in zip(message_ids, results) if deleted]
            if deleted_ids:
                with session_factory() as session:
                    for message_id in deleted_ids:
                        remove_tracked_message(session, chat_id, message_id)
                    session.commit()
            cleared = len(deleted_ids)
            return {"ok": True, "cleared": cleared, "failed": len(message_ids) - cleared}

        reply = ""
        actor = str(admin_id)
        if command.name in {"/start", "/help"}:
            try:
                await asyncio.to_thread(set_bot_commands, settings.telegram_bot_token)
            except Exception:
                # The help response must still work if Telegram command-menu sync is unavailable.
                pass
            reply = (
                "Remote Print Manager - Bot quản trị\n\n"
                "Lệnh quản lý:\n"
                 "/create [ten_khach_hang] [so_ngay|forever] [so_thiet_bi|unlimited] [100..5000|unlimited]\n"
                "/list - Xem toàn bộ thông tin mã\n"
                "/reissue [id] - Cấp lại mã khi khách hàng làm mất mã\n"
                "/revoke [id|ma] - Thu hồi mã\n"
                "/clear-chat - Xóa tin nhắn bot và người dùng đã ghi nhận\n\n"
                "Ví dụ:\n"
                "/create CongTyABC 30 1 500\n"
                 "/create CongTyABC forever unlimited 5000\n"
                "/reissue 38dddc98-b95a-48c9-b1b2-571df9c0f4ab"
            )
        elif command.name == "/create":
            label = command.args[0] if command.args else "customer"
            days_arg = command.args[1].lower() if len(command.args) > 1 else "30"
            devices_arg = command.args[2].lower() if len(command.args) > 2 else "1"
            radius_arg = command.args[3].lower() if len(command.args) > 3 else ""
            days = None if days_arg in {"forever", "permanent", "vĩnhviễn", "vinhvien"} else int(days_arg) if days_arg.isdigit() else -1
            max_devices = None if devices_arg in {"unlimited", "unlimited-devices", "vohan", "vôhạn"} else int(devices_arg) if devices_arg.isdigit() else -1
            unlimited_geofence = radius_arg in {"unlimited", "vohan", "vôhạn", "no-limit", "nolimit"}
            radius = None if unlimited_geofence else int(radius_arg) if radius_arg.isdigit() else -1
            if len(command.args) != 4 or (days is not None and not 1 <= days <= 365) or (max_devices is not None and not 1 <= max_devices <= 15) or (
                not unlimited_geofence and not 100 <= radius <= 5000
            ):
                reply = "Cú pháp: /create [tên] [số_ngày|forever] [số_thiết_bị|unlimited] [100..5000|unlimited]. Ví dụ: /create CongTyABC forever unlimited 5000"
            else:
                with session_factory() as session:
                    code = create_activation_code(
                        session, label=label, days=days, max_devices=max_devices,
                        geofence_policy=GEOFENCE_POLICY_UNLIMITED if unlimited_geofence else GEOFENCE_POLICY_LIMITED,
                        geofence_radius_meters=radius,
                        created_by=actor,
                    )
                    session.flush()
                    record = session.query(ActivationCode).filter(ActivationCode.code_hash == hash_token(code)).one()
                    record_id = record.id
                    code_hint = record.code_hint
                    created_at = record.created_at
                    expires_at = record.expires_at
                    session.commit()
                reply = (
                    "ĐÃ TẠO MÃ KÍCH HOẠT\n\n"
                    f"Mã: {code}\n"
                    f"Mã tham chiếu: {code_hint}...\n"
                    f"ID: {record_id}\n"
                    f"Khách hàng: {label}\n"
                    f"Thời hạn: {'Vĩnh viễn' if days is None else f'{days} ngày'}\n"
                    f"Thiết bị tối đa: {'Không giới hạn' if max_devices is None else max_devices}\n"
                    f"Phạm vi: {'Không giới hạn khoảng cách' if unlimited_geofence else f'{radius} mét từ máy chủ'}\n"
                    f"Tạo lúc: {format_vietnam_datetime(created_at)}\n"
                    f"Hết hạn: {format_vietnam_datetime(expires_at)}\n"
                    f"Đếm ngược: {format_remaining(expires_at)}\n"
                    "Trạng thái: ĐANG HOẠT ĐỘNG\n\n"
                    "Lưu ý: Mã đầy đủ chỉ hiển thị một lần. Hãy gửi mã cho khách hàng ngay."
                )
        elif command.name == "/list":
            with session_factory() as session:
                purge_revoked_activation_codes(session, actor)
                session.commit()
                records = session.query(ActivationCode).order_by(ActivationCode.created_at.desc()).all()
                now = utcnow()
                details = []
                for index, record in enumerate(records, 1):
                    expired = record.expires_at is not None and as_utc(record.expires_at) <= now
                    details.append(
                        f"{index}. {record.code_hint}... (mã đầy đủ không lưu trên server)\n"
                        f"   ID: {record.id}\n"
                        f"   Khách hàng: {record.label}\n"
                        f"   Trạng thái: {'HẾT HẠN' if expired else 'ĐANG HOẠT ĐỘNG'}\n"
                        f"   Thiết bị: {record.used_devices}/{record.max_devices if record.max_devices is not None else 'Không giới hạn'}\n"
                        f"   Phạm vi: {'Không giới hạn khoảng cách' if record.geofence_policy == GEOFENCE_POLICY_UNLIMITED else f'{record.geofence_radius_meters} mét'}\n"
                        f"   Tạo lúc: {format_vietnam_datetime(record.created_at)}\n"
                        f"   Hết hạn: {format_vietnam_datetime(record.expires_at)}\n"
                        f"   Đếm ngược: {format_remaining(record.expires_at, now)}"
                    )
                reply = "DANH SÁCH MÃ KÍCH HOẠT\n\n" + "\n\n".join(details) if details else "Chưa có mã khách hàng."
        elif command.name == "/reissue" and command.args:
            with session_factory() as session:
                result = reissue_activation_code(session, command.args[0], actor)
                if result is None:
                    reply = "Không tìm thấy mã còn hiệu lực theo ID. Hãy dùng /list để lấy ID chính xác."
                else:
                    code, record = result
                    session.commit()
                    reply = (
                        "ĐÃ CẤP LẠI MÃ KÍCH HOẠT\n\n"
                        f"Mã mới: {code}\n"
                        f"ID mới: {record.id}\n"
                        f"Khách hàng: {record.label}\n"
                        f"Thời hạn: {format_vietnam_datetime(record.expires_at)}\n"
                        f"Thiết bị tối đa: {'Không giới hạn' if record.max_devices is None else record.max_devices}\n"
                        f"Phạm vi: {'Không giới hạn khoảng cách' if record.geofence_policy == GEOFENCE_POLICY_UNLIMITED else f'{record.geofence_radius_meters} mét từ máy chủ'}\n"
                        f"Đếm ngược: {format_remaining(record.expires_at)}\n\n"
                        "Mã cũ đã vô hiệu hóa và thiết bị cũ phải kích hoạt lại. "
                        "Mã đầy đủ chỉ hiển thị trong tin nhắn này."
                    )
        elif command.name == "/revoke" and command.args:
            with session_factory() as session:
                revoked = revoke_activation_code(session, command.args[0], actor)
                session.commit()
            reply = "Đã thu hồi mã và vô hiệu hóa thiết bị liên quan." if revoked else "Không tìm thấy mã hoặc mã đã được thu hồi."
        else:
            reply = "Lệnh không hợp lệ. Gửi /help để xem hướng dẫn bằng tiếng Việt."

        # Telegram rejects messages over 4096 characters; preserve every record by splitting on entry boundaries.
        chunks = []
        current = ""
        for entry in reply.split("\n\n"):
            candidate = entry if not current else f"{current}\n\n{entry}"
            if len(candidate) > 3900 and current:
                chunks.append(current)
                current = entry
            else:
                current = candidate
        if current:
            chunks.append(current)
        sent_message_ids = []
        for chunk in chunks or [reply]:
            sent_message_ids.append(await asyncio.to_thread(send_message, settings.telegram_bot_token, chat_id, chunk))
        with session_factory() as session:
            for sent_message_id in sent_message_ids:
                record_message(session, chat_id, sent_message_id, "OUT")
            session.commit()
        return {"ok": True}

    return app


app = create_app()
