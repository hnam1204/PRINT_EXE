from datetime import timedelta

from app.database import ActivationCode, as_utc, create_session_factory, utcnow
from app.telegram_bot import create_activation_code, parse_command, revoke_activation_code


def test_parse_telegram_command_with_bot_suffix():
    command = parse_command("/create-code@rpm_bot Acme 30 2")

    assert command is not None
    assert command.name == "/create-code"
    assert command.args == ("Acme", "30", "2")


def test_create_and_revoke_customer_activation_code(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'activation.db'}")
    with session_factory() as session:
        code = create_activation_code(session, label="Acme", days=30, max_devices=2, created_by="admin")
        session.flush()
        record = session.query(ActivationCode).one()
        session.commit()

        assert len(code) == 12
        assert record.code_hint == code[:5]
        assert as_utc(record.expires_at) > utcnow() + timedelta(days=29)
        assert revoke_activation_code(session, code, "admin") is True
        assert record.revoked_at is not None


def test_create_perpetual_unlimited_activation_code(tmp_path):
    _, session_factory = create_session_factory(f"sqlite:///{tmp_path / 'perpetual.db'}")
    with session_factory() as session:
        create_activation_code(session, label="Enterprise", days=None, max_devices=None, created_by="admin")
        session.flush()
        record = session.query(ActivationCode).one()

        assert record.expires_at is None
        assert record.max_devices is None
