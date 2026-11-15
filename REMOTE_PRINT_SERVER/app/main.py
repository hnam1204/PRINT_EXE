from datetime import datetime, timedelta, timezone
import secrets
import hashlib
import json
import time
import asyncio

from fastapi import Depends, FastAPI, File, Header, HTTPException, Request, UploadFile, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, Response
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError, SQLAlchemyError

from .config import ConfigurationError, Settings
from .database import ActivationCode, AuditEvent, Device, Pairing, PairingCode, PrintJob, PrintJobTombstone, Printer, as_utc, create_session_factory, utcnow
from .schemas import (
    DeviceProfile,
    DeviceRegistration,
    DeviceRegistrationResponse,
    HeartbeatResponse,
    PairingDecision,
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
)
from .pairing_codes import decrypt_pairing_code, hash_pairing_code, issue_pairing_code
from .cleanup import run_cleanup
from .geofence import verify_location
from .storage import BlobStore, VercelBlobStore
from .security import enforce_rate_limit, hash_token, new_device_token, require_device, require_role
from .telegram_bot import (
    create_activation_code,
    delete_message,
    parse_command,
    record_message,
    remove_tracked_message,
    purge_revoked_activation_codes,
    revoke_activation_code,
    send_message,
    tracked_message_ids,
)


def _revoke_pairings_for_code(session, host_id: str, code: str, revoked_at: datetime) -> int:
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
    return len(pairings)


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
        )

    def verify_pairing_location(host: Device, payload: PairingRequest, now: datetime):
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

    app.add_middleware(
        CORSMiddleware,
        allow_origins=list(settings.allowed_origins),
        allow_credentials=False,
        allow_methods=["GET", "POST", "PATCH", "DELETE", "OPTIONS"],
        allow_headers=["Authorization", "Content-Type", "Idempotency-Key", "X-Enrollment-Secret", "X-Admin-Key"],
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
                if device is not None and device.activation_code_id not in (None, activation_code.id):
                    raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Device is already activated with another code")
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
                session.add(device)
            else:
                if device.is_revoked:
                    raise HTTPException(status_code=403, detail="Device is revoked")
                device.name = payload.name
                device.role = payload.role
                device.app_version = payload.appVersion
                device.token_hash = hash_token(token)
                if activation_code is not None and device.activation_code_id is None:
                    device.activation_code_id = activation_code.id
                device.public_key = payload.publicKey
                device.key_algorithm = payload.keyAlgorithm
                device.last_seen_at = utcnow()

            session.add(AuditEvent(actor_id=payload.deviceId, action="DEVICE_REGISTERED", target_type="DEVICE", target_id=payload.deviceId))
            session.commit()
            return DeviceRegistrationResponse(deviceId=device.id, deviceToken=token, role=device.role)

    @app.get("/api/v1/devices/me", response_model=DeviceProfile, tags=["Devices"])
    def get_device_profile(device: Device = Depends(require_device)):
        return DeviceProfile(
            deviceId=device.id,
            name=device.name,
            role=device.role,
            appVersion=device.app_version,
            isRevoked=device.is_revoked,
            lastSeenAt=device.last_seen_at,
            publicKey=device.public_key,
            keyAlgorithm=device.key_algorithm,
        )

    @app.post("/api/v1/devices/heartbeat", response_model=HeartbeatResponse, tags=["Devices"])
    def heartbeat(request: Request, device: Device = Depends(require_device)):
        with session_factory() as session:
            current = session.get(Device, device.id)
            if current is None or current.is_revoked:
                raise HTTPException(status_code=401, detail="Device is not active")
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
            location_result = verify_pairing_location(host, payload, now)
            existing = session.scalar(
                select(Pairing).where(
                    Pairing.client_id == device.id,
                    Pairing.host_id == host.id,
                    Pairing.status == "PENDING",
                    Pairing.expires_at > now,
                )
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
            session.add(pairing)
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_REQUESTED", target_type="DEVICE", target_id=host.id))
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
                approved_client_ids = set(session.scalars(select(Pairing.client_id).where(Pairing.host_id == device.id, Pairing.status == "APPROVED")).all())
                if pairing.client_id not in approved_client_ids and len(approved_client_ids) >= settings.max_clients_per_host:
                    raise HTTPException(status_code=409, detail=f"Host đã đủ {settings.max_clients_per_host} Client được phê duyệt")
            pairing.status = "APPROVED" if payload.approve else "REJECTED"
            pairing.allowed_printers = payload.allowedPrinters
            pairing.decided_at = now
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_APPROVED" if payload.approve else "PAIRING_REJECTED", target_type="PAIRING", target_id=pairing.id))
            session.commit()
            return pairing_response(pairing)

    @app.get("/api/v1/pairings/{pairing_id}", response_model=PairingResponse, tags=["Pairing"])
    def get_pairing_status(pairing_id: str, device: Device = Depends(require_device)):
        with session_factory() as session:
            pairing = session.get(Pairing, pairing_id)
            if pairing is None or device.id not in {pairing.host_id, pairing.client_id}:
                raise HTTPException(status_code=404, detail="Pairing not found")
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
            session.commit()
            return pairing_response(pairing)

    @app.get("/api/v1/devices/geofence", response_model=GeofenceResponse, tags=["Devices"])
    def get_geofence(device: Device = Depends(require_device)):
        require_role(device, "HOST")
        return GeofenceResponse(
            hostId=device.id,
            enabled=device.geofence_enabled,
            latitude=device.geofence_latitude,
            longitude=device.geofence_longitude,
            radiusMeters=device.geofence_radius_meters,
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
            return DeviceProfile(
                deviceId=target.id,
                name=target.name,
                role=target.role,
                appVersion=target.app_version,
                isRevoked=target.is_revoked,
                lastSeenAt=target.last_seen_at,
                publicKey=target.public_key,
                keyAlgorithm=target.key_algorithm,
            )

    @app.put("/api/v1/devices/geofence", response_model=GeofenceResponse, tags=["Devices"])
    def update_geofence(payload: GeofenceSettings, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        if payload.enabled and (payload.latitude is None or payload.longitude is None):
            raise HTTPException(status_code=422, detail="Latitude and longitude are required when geofence is enabled")
        with session_factory() as session:
            host = session.get(Device, device.id)
            host.geofence_enabled = payload.enabled
            host.geofence_latitude = payload.latitude
            host.geofence_longitude = payload.longitude
            host.geofence_radius_meters = payload.radiusMeters
            session.add(AuditEvent(actor_id=device.id, action="GEOFENCE_UPDATED", target_type="DEVICE", target_id=device.id))
            session.commit()
            return GeofenceResponse(hostId=host.id, enabled=host.geofence_enabled, latitude=host.geofence_latitude, longitude=host.geofence_longitude, radiusMeters=host.geofence_radius_meters)

    @app.post("/api/v1/pairing-codes/issue", response_model=PairingCodeResponse, status_code=201, tags=["Pairing codes"])
    def issue_code(payload: PairingCodeIssueRequest, device: Device = Depends(require_device)):
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
                _revoke_pairings_for_code(session, device.id, decrypt_pairing_code(
                    active_code.code_ciphertext, settings.enrollment_secret
                ), now)
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
        now = utcnow()
        with session_factory() as session:
            record = session.scalar(
                select(PairingCode).where(PairingCode.device_id == device.id).order_by(PairingCode.issued_at.desc())
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
            location_result = verify_pairing_location(host, location_payload(host.id, payload), utcnow())
            pairing = Pairing(
                host_id=host.id, client_id=device.id, code=payload.code,
                status="PENDING", expires_at=record.expires_at,
            )
            apply_location_evidence(pairing, payload, location_result)
            session.add(pairing)
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_REQUESTED", target_type="DEVICE", target_id=host.id))
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
        now = utcnow()
        with session_factory() as session:
            record = session.scalar(select(PairingCode).where(PairingCode.session_id == payload.sessionId, PairingCode.device_id == device.id))
            if record is None:
                raise HTTPException(status_code=404, detail="Pairing code not found")
            if record.status == "ACTIVE":
                record.status = "REVOKED"
                record.revoked_at = now
                _revoke_pairings_for_code(session, device.id, decrypt_pairing_code(
                    record.code_ciphertext, settings.enrollment_secret
                ), now)
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
                if pairing.allowed_printers:
                    printers = [item for item in printers if item.id in pairing.allowed_printers]
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
            if pairing.allowed_printers and printer.id not in pairing.allowed_printers:
                raise HTTPException(status_code=403, detail="Printer is not allowed for this pairing")
            host = session.get(Device, payload.hostId)
            if host is None:
                raise HTTPException(status_code=404, detail="Host not found")
            verify_pairing_location(host, location_payload(host.id, payload), utcnow())
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
            session.commit()
            return job_response(job)

    @app.get("/api/v1/internal/cleanup", tags=["Internal"])
    def internal_cleanup(dry_run: bool = False, authorization: str | None = Header(default=None)):
        if not settings.cron_secret or not authorization or not authorization.startswith("Bearer "):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Cron authorization required")
        if not secrets.compare_digest(authorization.removeprefix("Bearer ").strip(), settings.cron_secret):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid cron authorization")
        return run_cleanup(session_factory, engine, app.state.blob_store, dry_run=dry_run)

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
            reply = (
                "Remote Print Manager - Bot quản trị\n\n"
                "Các lệnh sử dụng:\n"
                "/create-code [ten_khach_hang] [so_ngay] [so_thiet_bi]\n"
                "/list-codes - Xem danh sách mã\n"
                "/revoke-code [ma] - Thu hồi mã\n\n"
                "/clear-chat - Xóa các tin nhắn bot đã ghi nhận\n\n"
                "Ví dụ:\n"

                "/create-code CongTyABC 30 1\n"
                "/create-code CongTyABC forever unlimited"
            )
        elif command.name == "/create-code":
            label = command.args[0] if command.args else "customer"
            days_arg = command.args[1].lower() if len(command.args) > 1 else "30"
            devices_arg = command.args[2].lower() if len(command.args) > 2 else "1"
            days = None if days_arg in {"forever", "permanent", "vĩnhviễn", "vinhvien"} else int(days_arg) if days_arg.isdigit() else -1
            max_devices = None if devices_arg in {"unlimited", "unlimited-devices", "vohan", "vôhạn"} else int(devices_arg) if devices_arg.isdigit() else -1
            if (days is not None and not 1 <= days <= 365) or (max_devices is not None and not 1 <= max_devices <= 15):
                reply = "Số ngày phải từ 1 đến 365 hoặc forever; số thiết bị phải từ 1 đến 15 hoặc unlimited."
            else:
                with session_factory() as session:
                    code = create_activation_code(session, label=label, days=days, max_devices=max_devices, created_by=actor)
                    session.commit()
                reply = (
                    f"Đã tạo mã kích hoạt khách hàng:\n{code}\n\n"
                    f"Tên khách hàng: {label}\n"
                    f"Thời hạn: {'Vĩnh viễn' if days is None else f'{days} ngày'}\n"
                    f"Số thiết bị tối đa: {'Không giới hạn' if max_devices is None else max_devices}\n\n"
                    "Lưu ý: Mã chỉ hiển thị một lần. Hãy gửi mã này cho khách hàng."
                )
        elif command.name == "/list-codes":
            with session_factory() as session:
                purge_revoked_activation_codes(session, actor)
                session.commit()
                records = session.query(ActivationCode).order_by(ActivationCode.created_at.desc()).limit(20).all()
                now = utcnow()
                reply = "\n".join(
                    f"{record.code_hint}... | {record.label} | {record.used_devices}/{record.max_devices if record.max_devices is not None else 'Không giới hạn'} | "
                    f"{'ĐÃ THU HỒI' if record.revoked_at else 'HẾT HẠN' if record.expires_at is not None and as_utc(record.expires_at) <= now else 'ĐANG HOẠT ĐỘNG'}"
                    for record in records
                ) or "Chưa có mã khách hàng."
        elif command.name == "/revoke-code" and command.args:
            with session_factory() as session:
                revoked = revoke_activation_code(session, command.args[0], actor)
                session.commit()
            reply = "Đã thu hồi mã kích hoạt." if revoked else "Không tìm thấy mã hoặc mã đã được thu hồi."
        else:
            reply = "Lệnh không hợp lệ. Gửi /help để xem hướng dẫn bằng tiếng Việt."

        sent_message_id = await asyncio.to_thread(send_message, settings.telegram_bot_token, chat_id, reply)
        with session_factory() as session:
            record_message(session, chat_id, sent_message_id, "OUT")
            session.commit()
        return {"ok": True}

    return app


app = create_app()
