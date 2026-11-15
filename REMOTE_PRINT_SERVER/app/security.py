import base64
import hashlib
import hmac
import secrets
from datetime import datetime, timedelta, timezone

from fastapi import Depends, Header, HTTPException, Request, status
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding
from cryptography.exceptions import InvalidSignature
from sqlalchemy import delete, func, select
from sqlalchemy.orm import Session

from .database import Device, RateLimitEvent


def hash_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def new_device_token() -> str:
    return secrets.token_urlsafe(32)


def tokens_match(candidate: str, stored_hash: str) -> bool:
    return hmac.compare_digest(hash_token(candidate), stored_hash)


def verify_device_reactivation_signature(public_key_b64: str, signature_b64: str, message: str) -> bool:
    """Verify proof of possession of the device's previously registered key."""
    try:
        public_key = serialization.load_der_public_key(base64.b64decode(public_key_b64, validate=True))
        signature = base64.b64decode(signature_b64, validate=True)
        public_key.verify(
            signature,
            message.encode("utf-8"),
            padding.PSS(mgf=padding.MGF1(hashes.SHA256()), salt_length=padding.PSS.MAX_LENGTH),
            hashes.SHA256(),
        )
        return True
    except (InvalidSignature, ValueError, TypeError, UnicodeError):
        return False


def enforce_rate_limit(session: Session, bucket: str, max_attempts: int, window_seconds: int) -> None:
    now = datetime.now(timezone.utc)
    cutoff = now - timedelta(seconds=window_seconds)
    session.execute(delete(RateLimitEvent).where(RateLimitEvent.created_at < cutoff))
    attempts = session.scalar(
        select(func.count(RateLimitEvent.id)).where(
            RateLimitEvent.bucket == bucket,
            RateLimitEvent.created_at >= cutoff,
        )
    ) or 0
    if attempts >= max_attempts:
        raise HTTPException(status_code=status.HTTP_429_TOO_MANY_REQUESTS, detail="Too many attempts")
    session.add(RateLimitEvent(bucket=bucket, created_at=now))


def require_device(request: Request, authorization: str | None = Header(default=None)) -> Device:
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Bearer token required")

    token = authorization.removeprefix("Bearer ").strip()
    if not token or len(token) > 256:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid device token")

    with request.app.state.session_factory() as session:
        device = session.scalar(select(Device).where(Device.token_hash == hash_token(token)))
        if device is None or device.is_revoked:
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid device token")
        session.commit()
        session.expunge(device)
        return device


def require_role(device: Device, role: str) -> Device:
    if device.role != role:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail=f"{role} role required")
    return device
