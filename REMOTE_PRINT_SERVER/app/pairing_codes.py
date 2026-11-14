import hashlib
import secrets
import base64
from datetime import datetime, timezone

from cryptography.fernet import Fernet
from sqlalchemy.exc import IntegrityError
from sqlalchemy import select
from sqlalchemy.orm import Session

from .database import PairingCode

CODE_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
CODE_LENGTH = 6
MAX_ISSUE_ATTEMPTS = 100


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def hash_pairing_code(code: str) -> str:
    return hashlib.sha256(code.encode("ascii")).hexdigest()


def _fernet(secret: str) -> Fernet:
    key = base64.urlsafe_b64encode(hashlib.sha256(secret.encode("utf-8")).digest())
    return Fernet(key)


def encrypt_pairing_code(code: str, secret: str) -> str:
    return _fernet(secret).encrypt(code.encode("ascii")).decode("ascii")


def decrypt_pairing_code(ciphertext: str, secret: str) -> str:
    return _fernet(secret).decrypt(ciphertext.encode("ascii")).decode("ascii")


def random_pairing_code() -> str:
    return "".join(secrets.choice(CODE_ALPHABET) for _ in range(CODE_LENGTH))


def issue_pairing_code(session: Session, device_id: str, session_id: str, expires_at: datetime, secret: str) -> tuple[PairingCode, str]:
    existing = session.scalar(select(PairingCode).where(PairingCode.session_id == session_id))
    if existing is not None:
        if existing.device_id != device_id:
            raise ValueError("Session belongs to another device")
        if existing.status == "ACTIVE":
            return existing, decrypt_pairing_code(existing.code_ciphertext, secret)
        raise ValueError("Session already has an inactive pairing code")
    for _ in range(MAX_ISSUE_ATTEMPTS):
        plain_code = random_pairing_code()
        try:
            with session.begin_nested():
                record = PairingCode(
                    session_id=session_id,
                    device_id=device_id,
                    code_hash=hash_pairing_code(plain_code),
                    code_ciphertext=encrypt_pairing_code(plain_code, secret),
                    issued_at=utcnow(),
                    expires_at=expires_at,
                    status="ACTIVE",
                )
                session.add(record)
                session.flush()
                return record, plain_code
        except IntegrityError:
            existing = session.scalar(select(PairingCode).where(PairingCode.session_id == session_id))
            if existing is not None:
                if existing.device_id != device_id:
                    raise ValueError("Session belongs to another device")
                if existing.status == "ACTIVE":
                    return existing, decrypt_pairing_code(existing.code_ciphertext, secret)
                raise ValueError("Session already has an inactive pairing code")
            continue
    raise RuntimeError("Unable to issue a globally unique pairing code")
