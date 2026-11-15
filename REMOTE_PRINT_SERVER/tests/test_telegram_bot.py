from datetime import timedelta

import pytest

from app.database import ActivationCode, AuditEvent, Device, as_utc, create_session_factory, utcnow
from app.telegram_bot import create_activation_code, parse_command, purge_revoked_activation_codes, record_message, remove_tracked_message, revoke_activation_code, tracked_message_ids


def test_parse_telegram_command_with_bot_suffix():
    command = parse_command("/create-code@rpm_bot Acme 30 2")

    assert command is not None
    assert command.name == "/create-code"
    assert command.args == ("Acme", "30", "2")


def test_parse_telegram_command_accepts_geofence_radius():
    command = parse_command("/create-code Acme 30 2 500")
    assert command is not None
    assert command.args == ("Acme", "30", "2", "500")


def test_create_and_revoke_customer_activation_code(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'activation.db'}")
    with session_factory() as session:
        code = create_activation_code(session, label="Acme", days=30, max_devices=2, created_by="admin")
        session.flush()
        record = session.query(ActivationCode).one()
        session.add(Device(
            id="device-01", name="Device", role="CLIENT", app_version="1.0.0",
            token_hash="token-hash", activation_code_id=record.id,
        ))
        session.commit()

        assert len(code) == 12
        assert record.code_hint == code[:5]
        assert as_utc(record.expires_at) > utcnow() + timedelta(days=29)
        assert revoke_activation_code(session, code, "admin") is True
        session.flush()
        assert session.query(ActivationCode).count() == 0
        device = session.query(Device).one()
        assert device.is_revoked is True
        assert device.activation_code_id is None
        assert session.query(AuditEvent).filter(AuditEvent.action == "CUSTOMER_ACTIVATION_CODE_DELETED").count() == 1


def test_create_perpetual_unlimited_activation_code(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'perpetual.db'}")
    with session_factory() as session:
        create_activation_code(session, label="Enterprise", days=None, max_devices=None, created_by="admin")
        session.flush()
        record = session.query(ActivationCode).one()

        assert record.expires_at is None
        assert record.max_devices is None


def test_activation_code_geofence_radius_is_bounded(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'radius.db'}")
    with session_factory() as session:
        create_activation_code(session, label="Customer", days=30, max_devices=1, geofence_radius_meters=500, created_by="admin")
        session.flush()
        assert session.query(ActivationCode).one().geofence_radius_meters == 500
        with pytest.raises(ValueError):
            create_activation_code(session, label="Invalid", days=30, max_devices=1, geofence_radius_meters=1001, created_by="admin")


def test_telegram_message_tracking_supports_chat_cleanup(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'telegram.db'}")
    with session_factory() as session:
        record_message(session, 123, 10, "IN")
        record_message(session, 123, 11, "OUT")
        session.commit()

    with session_factory() as session:
        assert set(tracked_message_ids(session, 123)) == {10, 11}
        remove_tracked_message(session, 123, 11)
        session.commit()

    with session_factory() as session:
        assert tracked_message_ids(session, 123) == [10]


def test_purge_revoked_codes_removes_legacy_revoked_rows(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'purge.db'}")
    with session_factory() as session:
        code = create_activation_code(session, label="Legacy", days=30, max_devices=1, created_by="admin")
        session.flush()
        session.query(ActivationCode).one().revoked_at = utcnow()
        session.commit()

    with session_factory() as session:
        assert purge_revoked_activation_codes(session, "admin") == 1
        session.commit()
        assert session.query(ActivationCode).count() == 0
