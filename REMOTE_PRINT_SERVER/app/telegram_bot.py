import json
import secrets
import urllib.request
from dataclasses import dataclass
from datetime import timedelta

from .database import ActivationCode, AuditEvent, utcnow
from .security import hash_token

CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"


@dataclass(frozen=True)
class TelegramCommand:
    name: str
    args: tuple[str, ...]


def parse_command(text: str) -> TelegramCommand | None:
    parts = text.strip().split()
    if not parts or not parts[0].startswith("/"):
        return None
    return TelegramCommand(parts[0].split("@", 1)[0].lower(), tuple(parts[1:]))


def generate_activation_code(length: int = 12) -> str:
    return "".join(secrets.choice(CODE_ALPHABET) for _ in range(length))


def create_activation_code(session, *, label: str, days: int | None, max_devices: int | None, created_by: str) -> str:
    code = generate_activation_code()
    session.add(ActivationCode(
        code_hash=hash_token(code),
        code_hint=code[:5],
        label=label[:160] or "customer",
        max_devices=max_devices,
        expires_at=utcnow() + timedelta(days=days) if days is not None else None,
        created_by=created_by,
    ))
    session.add(AuditEvent(
        actor_id=created_by,
        action="CUSTOMER_ACTIVATION_CODE_CREATED",
        target_type="ACTIVATION_CODE",
        details={"label": label[:160], "days": days, "maxDevices": max_devices, "codeHint": code[:5]},
    ))
    return code


def revoke_activation_code(session, code: str, actor_id: str) -> bool:
    record = session.query(ActivationCode).filter(ActivationCode.code_hash == hash_token(code.strip().upper())).one_or_none()
    if record is None or record.revoked_at is not None:
        return False
    record.revoked_at = utcnow()
    session.add(AuditEvent(
        actor_id=actor_id,
        action="CUSTOMER_ACTIVATION_CODE_REVOKED",
        target_type="ACTIVATION_CODE",
        target_id=record.id,
        details={"codeHint": record.code_hint},
    ))
    return True


def send_message(bot_token: str, chat_id: int, text: str) -> None:
    payload = json.dumps({"chat_id": chat_id, "text": text}).encode("utf-8")
    request = urllib.request.Request(
        f"https://api.telegram.org/bot{bot_token}/sendMessage",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=10) as response:
        if response.status >= 300:
            raise RuntimeError("Telegram API request failed")
