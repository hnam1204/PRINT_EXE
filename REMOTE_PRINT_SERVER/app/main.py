from datetime import datetime, timedelta, timezone
import secrets
import hashlib
import json
import time

from fastapi import Depends, FastAPI, Header, HTTPException, Request, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from sqlalchemy import select

from .config import ConfigurationError, Settings
from .database import AuditEvent, Device, Pairing, PrintJob, Printer, as_utc, create_session_factory, utcnow
from .schemas import (
    DeviceProfile,
    DeviceRegistration,
    DeviceRegistrationResponse,
    HeartbeatResponse,
    PairingDecision,
    PairingRequest,
    PairingResponse,
    PendingPairing,
    PrintJobRequest,
    PrintJobResponse,
    PrinterRegistration,
    PrinterResponse,
)
from .security import enforce_rate_limit, hash_token, new_device_token, require_device, require_role


def create_app(settings: Settings | None = None, database_url: str | None = None) -> FastAPI:
    settings = settings or Settings.from_env()
    db_url = database_url or settings.database_url
    engine, session_factory = create_session_factory(db_url)

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
        return {
            "status": "ok",
            "apiVersion": settings.api_version,
            "environment": settings.environment,
            "uptimeSeconds": round(time.monotonic() - app.state.started_at, 3),
            "dependencies": {"database": "configured"},
            "timestamp": datetime.now(timezone.utc).isoformat(),
        }

    @app.post("/api/v1/devices/register", response_model=DeviceRegistrationResponse, status_code=201, tags=["Devices"])
    def register_device(
        payload: DeviceRegistration,
        request: Request,
        enrollment_secret: str | None = Header(default=None, alias="X-Enrollment-Secret"),
    ):
        if not enrollment_secret or not settings.enrollment_secret or not secrets.compare_digest(enrollment_secret, settings.enrollment_secret):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid enrollment secret")

        with session_factory() as session:
            enforce_rate_limit(
                session,
                f"device-register:{request.client.host if request.client else 'unknown'}",
                settings.rate_limit_max_attempts,
                settings.rate_limit_window_seconds,
            )
            device = session.scalar(select(Device).where(Device.id == payload.deviceId))
            token = new_device_token()
            if device is None:
                device = Device(
                    id=payload.deviceId,
                    name=payload.name,
                    role=payload.role,
                    app_version=payload.appVersion,
                    token_hash=hash_token(token),
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
            existing = session.scalar(
                select(Pairing).where(
                    Pairing.client_id == device.id,
                    Pairing.host_id == host.id,
                    Pairing.status == "PENDING",
                    Pairing.expires_at > now,
                )
            )
            if existing:
                return PairingResponse(
                    pairingId=existing.id, hostId=existing.host_id, clientId=existing.client_id,
                    code=existing.code, status=existing.status, expiresAt=existing.expires_at,
                )
            code = f"{secrets.randbelow(1_000_000):06d}"
            pairing = Pairing(
                host_id=host.id,
                client_id=device.id,
                code=code,
                status="PENDING",
                expires_at=expires_at,
            )
            session.add(pairing)
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_REQUESTED", target_type="DEVICE", target_id=host.id))
            session.commit()
            return PairingResponse(
                pairingId=pairing.id, hostId=pairing.host_id, clientId=pairing.client_id,
                code=pairing.code, status=pairing.status, expiresAt=pairing.expires_at,
            )

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
            return [
                PendingPairing(pairingId=pairing.id, clientId=pairing.client_id, clientName=name, code=pairing.code, expiresAt=pairing.expires_at)
                for pairing, name in rows
            ]

    @app.post("/api/v1/pairings/{pairing_id}/decision", response_model=PairingResponse, tags=["Pairing"])
    def decide_pairing(pairing_id: str, payload: PairingDecision, device: Device = Depends(require_device)):
        require_role(device, "HOST")
        now = utcnow()
        with session_factory() as session:
            pairing = session.get(Pairing, pairing_id)
            if pairing is None or pairing.host_id != device.id:
                raise HTTPException(status_code=404, detail="Pairing not found")
            if pairing.status != "PENDING" or as_utc(pairing.expires_at) <= now:
                raise HTTPException(status_code=409, detail="Pairing is no longer pending")
            pairing.status = "APPROVED" if payload.approve else "REJECTED"
            pairing.allowed_printers = payload.allowedPrinters
            pairing.decided_at = now
            session.add(AuditEvent(actor_id=device.id, action="PAIRING_APPROVED" if payload.approve else "PAIRING_REJECTED", target_type="PAIRING", target_id=pairing.id))
            session.commit()
            return PairingResponse(
                pairingId=pairing.id, hostId=pairing.host_id, clientId=pairing.client_id,
                code=pairing.code, status=pairing.status, expiresAt=pairing.expires_at,
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
            pairing = session.scalar(select(Pairing).where(Pairing.host_id == payload.hostId, Pairing.client_id == device.id, Pairing.status == "APPROVED"))
            printer = session.get(Printer, payload.printerId)
            if pairing is None or printer is None or printer.host_id != payload.hostId or not printer.is_shared:
                raise HTTPException(status_code=403, detail="Approved shared printer access required")
            if pairing.allowed_printers and printer.id not in pairing.allowed_printers:
                raise HTTPException(status_code=403, detail="Printer is not allowed for this pairing")
            job = PrintJob(
                idempotency_key=idempotency_key, client_id=device.id, host_id=payload.hostId,
                printer_id=payload.printerId, request_hash=request_hash, metadata_json=payload.metadata,
            )
            session.add(job)
            session.add(AuditEvent(actor_id=device.id, action="PRINT_JOB_CREATED", target_type="PRINT_JOB", target_id=job.id))
            session.commit()
            return PrintJobResponse(
                jobId=job.id, idempotencyKey=job.idempotency_key, clientId=job.client_id,
                hostId=job.host_id, printerId=job.printer_id, status=job.status, metadata=job.metadata_json,
            )

    return app


app = create_app()
