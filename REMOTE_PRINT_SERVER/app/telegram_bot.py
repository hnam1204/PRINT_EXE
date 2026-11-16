import json
import secrets
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

from .database import ActivationCode, AuditEvent, Device, TelegramMessage, utcnow
from .activation_policy import (
    GEOFENCE_POLICY_LIMITED,
    GEOFENCE_POLICY_UNLIMITED,
    MAX_GEOFENCE_RADIUS_METERS,
    MIN_GEOFENCE_RADIUS_METERS,
    normalize_geofence_policy,
)
from .security import hash_token

CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
VIETNAM_TIMEZONE = ZoneInfo("Asia/Ho_Chi_Minh")


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


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


def format_vietnam_datetime(value: datetime | None) -> str:
    if value is None:
        return "Không hết hạn"
    formatted = _as_utc(value).astimezone(VIETNAM_TIMEZONE).strftime("%d/%m/%Y %H:%M:%S")
    return f"{formatted} (giờ Việt Nam)"


def format_remaining(expires_at: datetime | None, now: datetime | None = None) -> str:
    if expires_at is None:
        return "Vĩnh viễn"
    remaining = _as_utc(expires_at) - _as_utc(now or utcnow())
    if remaining.total_seconds() <= 0:
        return "ĐÃ HẾT HẠN"
    total_seconds = int(remaining.total_seconds())
    days, remainder = divmod(total_seconds, 86400)
    hours, remainder = divmod(remainder, 3600)
    minutes, seconds = divmod(remainder, 60)
    return f"Còn lại: {days} ngày {hours:02d} giờ {minutes:02d} phút {seconds:02d} giây"


def create_activation_code(
    session,
    *,
    label: str,
    days: int | None,
    max_devices: int | None,
    created_by: str,
    geofence_radius_meters: int | None = 1000,
    geofence_policy: str = GEOFENCE_POLICY_LIMITED,
) -> str:
    policy, radius = normalize_geofence_policy(geofence_policy, geofence_radius_meters)
    code = generate_activation_code()
    session.add(ActivationCode(
        code_hash=hash_token(code),
        code_hint=code[:5],
        label=label[:160] or "customer",
        max_devices=max_devices,
        geofence_policy=policy,
        geofence_radius_meters=radius,
        expires_at=utcnow() + timedelta(days=days) if days is not None else None,
        created_by=created_by,
    ))
    session.add(AuditEvent(
        actor_id=created_by,
        action="CUSTOMER_ACTIVATION_CODE_CREATED",
        target_type="ACTIVATION_CODE",
        details={
            "label": label[:160], "days": days, "maxDevices": max_devices,
            "geofencePolicy": policy, "geofenceRadiusMeters": radius, "codeHint": code[:5],
        },
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
    identifier = code.strip()
    record = session.query(ActivationCode).filter(ActivationCode.id == identifier).one_or_none()
    if record is None:
        record = session.query(ActivationCode).filter(ActivationCode.code_hash == hash_token(identifier.upper())).one_or_none()
    if record is None:
        return False
    _delete_activation_code_record(session, record, actor_id, already_revoked=record.revoked_at is not None)
    return True


def reissue_activation_code(session, identifier: str, actor_id: str) -> tuple[str, ActivationCode] | None:
    """Replace a lost code without ever storing or recovering its plaintext."""
    record = session.query(ActivationCode).filter(ActivationCode.id == identifier.strip()).one_or_none()
    if record is None or (record.expires_at is not None and _as_utc(record.expires_at) <= utcnow()):
        return None

    old_id = record.id
    code = generate_activation_code()
    replacement = ActivationCode(
        code_hash=hash_token(code),
        code_hint=code[:5],
        label=record.label,
        max_devices=record.max_devices,
        geofence_policy=record.geofence_policy,
        geofence_radius_meters=record.geofence_radius_meters,
        expires_at=record.expires_at,
        created_by=actor_id,
    )
    devices = session.query(Device).filter(Device.activation_code_id == old_id).all()
    for device in devices:
        device.is_revoked = True
        device.activation_code_id = None

    session.add(replacement)
    session.flush()
    session.add(AuditEvent(
        actor_id=actor_id,
        action="CUSTOMER_ACTIVATION_CODE_REISSUED",
        target_type="ACTIVATION_CODE",
        target_id=replacement.id,
        details={"replacedId": old_id, "codeHint": replacement.code_hint, "devicesRevoked": len(devices)},
    ))
    session.delete(record)
    return code, replacement


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


def set_bot_commands(bot_token: str) -> bool:
    payload = json.dumps({
        "commands": [
            {"command": "create", "description": "Tạo mã khách hàng"},
            {"command": "list", "description": "Xem danh sách mã"},
            {"command": "reissue", "description": "Cấp lại mã bị mất"},
            {"command": "revoke", "description": "Thu hồi mã"},
            {"command": "clear-chat", "description": "Xóa tin nhắn đoạn chat"},
        ]
    }).encode("utf-8")
    request = urllib.request.Request(
        f"https://api.telegram.org/bot{bot_token}/setMyCommands",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=10) as response:
        body = json.loads(response.read().decode("utf-8"))
        return response.status < 300 and bool(body.get("ok"))


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
