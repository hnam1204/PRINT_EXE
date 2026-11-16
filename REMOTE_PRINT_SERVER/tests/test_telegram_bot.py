from datetime import datetime, timedelta, timezone

import pytest

from app.database import ActivationCode, AuditEvent, Device, as_utc, create_session_factory, utcnow
from app.telegram_bot import create_activation_code, format_remaining, format_vietnam_datetime, parse_command, purge_revoked_activation_codes, record_message, remove_tracked_message, reissue_activation_code, revoke_activation_code, tracked_message_ids


def test_parse_telegram_command_with_bot_suffix():
    command = parse_command("/create@rpm_bot Acme 30 2 500")

    assert command is not None
    assert command.name == "/create"
    assert command.args == ("Acme", "30", "2", "500")


def test_parse_telegram_command_accepts_geofence_radius():
    command = parse_command("/create Acme 30 2 500")
    assert command is not None
    assert command.args == ("Acme", "30", "2", "500")


def test_parse_telegram_command_supports_short_management_names():
    assert parse_command("/create Acme forever unlimited 1000").name == "/create"
    assert parse_command("/list").name == "/list"
    assert parse_command("/revoke ABC123").name == "/revoke"
    assert parse_command("/reissue ABC123").name == "/reissue"


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


def test_reissue_replaces_code_and_revokes_old_devices(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'reissue.db'}")
    with session_factory() as session:
        old_code = create_activation_code(session, label="Acme", days=30, max_devices=3, geofence_radius_meters=500, created_by="admin")
        session.flush()
        old_record = session.query(ActivationCode).one()
        session.add(Device(
            id="device-01", name="Device", role="CLIENT", app_version="1.0.0",
            token_hash="token-hash", activation_code_id=old_record.id,
        ))
        session.commit()
        old_id = old_record.id

        result = reissue_activation_code(session, old_id, "admin")
        assert result is not None
        new_code, new_record = result
        session.commit()

        assert new_code != old_code
        assert new_record.label == "Acme"
        assert new_record.max_devices == 3
        assert new_record.geofence_radius_meters == 500
        assert session.get(ActivationCode, old_id) is None
        device = session.get(Device, "device-01")
        assert device.is_revoked is True
        assert device.activation_code_id is None


def test_activation_expiry_formats_in_vietnam_time_and_counts_down():
    created = datetime(2026, 11, 15, 0, 0, tzinfo=timezone.utc)
    expires = created + timedelta(days=1, hours=2, minutes=3, seconds=4)
    assert format_vietnam_datetime(created) == "15/11/2026 07:00:00 (giờ Việt Nam)"
    assert format_remaining(expires, created) == "Còn lại: 1 ngày 02 giờ 03 phút 04 giây"
    assert format_remaining(None, created) == "Vĩnh viễn"


def test_activation_code_geofence_radius_is_bounded(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'radius.db'}")
    with session_factory() as session:
        create_activation_code(session, label="Customer", days=30, max_devices=1, geofence_radius_meters=500, created_by="admin")
        session.flush()
        assert session.query(ActivationCode).one().geofence_radius_meters == 500
        with pytest.raises(ValueError):
            create_activation_code(session, label="Invalid", days=30, max_devices=1, geofence_radius_meters=5001, created_by="admin")


def test_activation_code_supports_five_kilometers_and_unlimited_policy(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'policy.db'}")
    with session_factory() as session:
        create_activation_code(
            session, label="Five km", days=30, max_devices=1,
            geofence_radius_meters=5000, created_by="admin",
        )
        create_activation_code(
            session, label="Unlimited", days=None, max_devices=None,
            geofence_policy="UNLIMITED", geofence_radius_meters=None, created_by="admin",
        )
        session.flush()
        records = {record.label: record for record in session.query(ActivationCode).all()}
        assert records["Unlimited"].geofence_policy == "UNLIMITED"
        assert records["Unlimited"].geofence_radius_meters is None
        assert records["Five km"].geofence_policy == "LIMITED"
        assert records["Five km"].geofence_radius_meters == 5000


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


def test_telegram_message_tracking_keeps_all_messages_for_cleanup(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'telegram-all.db'}")
    with session_factory() as session:
        for message_id in range(1, 126):
            record_message(session, 123, message_id, "IN" if message_id % 2 else "OUT")
        session.commit()

    with session_factory() as session:
        assert len(tracked_message_ids(session, 123)) == 125


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
