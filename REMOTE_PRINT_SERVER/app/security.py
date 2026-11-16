import base64
import hashlib
import hmac
import secrets
import json
import base64
from datetime import datetime, timedelta, timezone

from fastapi import Depends, Header, HTTPException, Request, status
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding
from cryptography.exceptions import InvalidSignature
from sqlalchemy import delete, func, select
from sqlalchemy.orm import Session

from .database import ActivationCode, Device, RateLimitEvent, as_utc, utcnow


def hash_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def new_device_token() -> str:
    return secrets.token_urlsafe(32)


def issue_realtime_session(device: Device, shared_secret: str, ttl_seconds: int = 300) -> tuple[str, datetime]:
    if not shared_secret:
        raise ValueError("Realtime gateway shared secret is not configured")
    expires_at = datetime.now(timezone.utc) + timedelta(seconds=ttl_seconds)
    payload = {
        "deviceId": device.id,
        "role": device.role,
        "exp": int(expires_at.timestamp()),
        "jti": secrets.token_urlsafe(12),
    }
    encoded = base64.urlsafe_b64encode(
        json.dumps(payload, separators=(",", ":"), sort_keys=True).encode("utf-8")
    ).rstrip(b"=").decode("ascii")
    signature = hmac.new(shared_secret.encode("utf-8"), encoded.encode("ascii"), hashlib.sha256).hexdigest()
    return f"{encoded}.{signature}", expires_at


def verify_realtime_session(token: str, shared_secret: str) -> dict | None:
    try:
        encoded, signature = token.split(".", 1)
        expected = hmac.new(shared_secret.encode("utf-8"), encoded.encode("ascii"), hashlib.sha256).hexdigest()
        if not hmac.compare_digest(signature, expected):
            return None
        padded = encoded + "=" * (-len(encoded) % 4)
        payload = json.loads(base64.urlsafe_b64decode(padded).decode("utf-8"))
        if int(payload["exp"]) <= int(datetime.now(timezone.utc).timestamp()):
            return None
        if not payload.get("deviceId") or payload.get("role") not in {"HOST", "CLIENT"}:
            return None
        return payload
    except (ValueError, KeyError, TypeError, json.JSONDecodeError, base64.binascii.Error):
        return None


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
        if device.activation_code_id:
            activation = session.get(ActivationCode, device.activation_code_id)
            if activation is None or activation.revoked_at is not None or (
                activation.expires_at is not None and as_utc(activation.expires_at) <= utcnow()
            ):
                raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Activation code is no longer active")
        session.commit()
        session.expunge(device)
        return device


def require_role(device: Device, role: str) -> Device:
    if device.role != role:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail=f"{role} role required")
    return device
