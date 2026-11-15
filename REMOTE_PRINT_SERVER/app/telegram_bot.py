import json
import secrets
import urllib.request
from dataclasses import dataclass
from datetime import timedelta

from .database import ActivationCode, AuditEvent, Device, TelegramMessage, utcnow
from .security import hash_token

CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
MIN_GEOFENCE_RADIUS_METERS = 1
MAX_GEOFENCE_RADIUS_METERS = 1000


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


def create_activation_code(session, *, label: str, days: int | None, max_devices: int | None, created_by: str, geofence_radius_meters: int = MAX_GEOFENCE_RADIUS_METERS) -> str:
    if not MIN_GEOFENCE_RADIUS_METERS <= geofence_radius_meters <= MAX_GEOFENCE_RADIUS_METERS:
        raise ValueError(f"Phạm vi phải từ {MIN_GEOFENCE_RADIUS_METERS} đến {MAX_GEOFENCE_RADIUS_METERS} mét.")
    code = generate_activation_code()
    session.add(ActivationCode(
        code_hash=hash_token(code),
        code_hint=code[:5],
        label=label[:160] or "customer",
        max_devices=max_devices,
        geofence_radius_meters=geofence_radius_meters,
        expires_at=utcnow() + timedelta(days=days) if days is not None else None,
        created_by=created_by,
    ))
    session.add(AuditEvent(
        actor_id=created_by,
        action="CUSTOMER_ACTIVATION_CODE_CREATED",
        target_type="ACTIVATION_CODE",
        details={"label": label[:160], "days": days, "maxDevices": max_devices, "geofenceRadiusMeters": geofence_radius_meters, "codeHint": code[:5]},
    ))
    return code


def _delete_activation_code_record(session, record: ActivationCode, actor_id: str, *, already_revoked: bool = False) -> None:
    revoked_at = record.revoked_at or utcnow()
    devices = session.query(Device).filter(Device.activation_code_id == record.id).all()
    for device in devices:
        device.is_revoked = True
        device.activation_code_id = None
    session.add(AuditEvent(
        actor_id=actor_id,
        action="CUSTOMER_ACTIVATION_CODE_PURGED" if already_revoked else "CUSTOMER_ACTIVATION_CODE_DELETED",
        target_type="ACTIVATION_CODE",
        target_id=record.id,
        details={"codeHint": record.code_hint, "revokedAt": revoked_at.isoformat(), "devicesRevoked": len(devices)},
    ))
    session.delete(record)


def revoke_activation_code(session, code: str, actor_id: str) -> bool:
    record = session.query(ActivationCode).filter(ActivationCode.code_hash == hash_token(code.strip().upper())).one_or_none()
    if record is None:
        return False
    _delete_activation_code_record(session, record, actor_id, already_revoked=record.revoked_at is not None)
    return True


def purge_revoked_activation_codes(session, actor_id: str) -> int:
    records = session.query(ActivationCode).filter(ActivationCode.revoked_at.is_not(None)).all()
    for record in records:
        _delete_activation_code_record(session, record, actor_id, already_revoked=True)
    return len(records)


def record_message(session, chat_id: int, message_id: int, direction: str) -> None:
    if message_id <= 0:
        return
    existing = session.query(TelegramMessage).filter(
        TelegramMessage.chat_id == str(chat_id), TelegramMessage.message_id == message_id
    ).one_or_none()
    if existing is None:
        session.add(TelegramMessage(chat_id=str(chat_id), message_id=message_id, direction=direction))


def tracked_message_ids(session, chat_id: int) -> list[int]:
    return [row.message_id for row in session.query(TelegramMessage)
            .filter(TelegramMessage.chat_id == str(chat_id))
            .order_by(TelegramMessage.created_at.desc()).all()]


def remove_tracked_message(session, chat_id: int, message_id: int) -> None:
    session.query(TelegramMessage).filter(
        TelegramMessage.chat_id == str(chat_id), TelegramMessage.message_id == message_id
    ).delete(synchronize_session=False)


def send_message(bot_token: str, chat_id: int, text: str) -> int:
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
        body = json.loads(response.read().decode("utf-8"))
        return int(((body.get("result") or {}).get("message_id")) or 0)


def delete_message(bot_token: str, chat_id: int, message_id: int) -> bool:
    payload = json.dumps({"chat_id": chat_id, "message_id": message_id}).encode("utf-8")
    request = urllib.request.Request(
        f"https://api.telegram.org/bot{bot_token}/deleteMessage",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            body = json.loads(response.read().decode("utf-8"))
            return response.status < 300 and bool(body.get("ok"))
    except Exception:
        return False
