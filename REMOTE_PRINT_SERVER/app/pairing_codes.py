import hashlib
import secrets
from datetime import datetime, timezone

from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from .database import PairingCode

CODE_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
CODE_LENGTH = 6
MAX_ISSUE_ATTEMPTS = 100


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def hash_pairing_code(code: str) -> str:
    return hashlib.sha256(code.encode("ascii")).hexdigest()


def random_pairing_code() -> str:
    return "".join(secrets.choice(CODE_ALPHABET) for _ in range(CODE_LENGTH))


def issue_pairing_code(session: Session, device_id: str, session_id: str, expires_at: datetime) -> tuple[PairingCode, str]:
    for _ in range(MAX_ISSUE_ATTEMPTS):
        plain_code = random_pairing_code()
        record = PairingCode(
            session_id=session_id,
            device_id=device_id,
            code_hash=hash_pairing_code(plain_code),
            issued_at=utcnow(),
            expires_at=expires_at,
            status="ACTIVE",
        )
        session.add(record)
        try:
            session.flush()
            return record, plain_code
        except IntegrityError:
            session.rollback()
            session.begin()
    raise RuntimeError("Unable to issue a globally unique pairing code")
