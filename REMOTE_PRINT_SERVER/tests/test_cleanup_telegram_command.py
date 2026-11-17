import os
from datetime import timedelta

from fastapi.testclient import TestClient

from app.database import AuditEvent, utcnow
from app.main import create_app


def test_telegram_cleanup_command_runs_only_for_configured_admin(monkeypatch, tmp_path):
    monkeypatch.setenv("RPM_SERVER_ENV", "development")
    monkeypatch.setenv("ALLOWED_ORIGINS", "http://localhost:3000")
    monkeypatch.setenv("ENROLLMENT_SECRET", "test-enrollment-secret")
    monkeypatch.setenv("CRON_SECRET", "test-cron-secret")
    monkeypatch.setenv("TELEGRAM_BOT_TOKEN", "telegram-test-token")
    monkeypatch.setenv("TELEGRAM_ADMIN_IDS", "12345")
    monkeypatch.setenv("TELEGRAM_WEBHOOK_SECRET", "telegram-webhook-secret")

    app = create_app(database_url=f"sqlite:///{tmp_path / 'telegram-cleanup.db'}")
    old_event = AuditEvent(id="old-audit", action="TEST", target_type="TEST", created_at=utcnow() - timedelta(hours=73))
    with app.state.session_factory() as session:
        session.add(old_event)
        session.commit()

    sent = []

    def fake_send_message(_token, _chat_id, text):
        sent.append(text)
        return len(sent)

    monkeypatch.setattr("app.main.send_message", fake_send_message)
    client = TestClient(app)
    payload = {
        "message": {
            "message_id": 10,
            "text": "/cleanup dry-run",
            "from": {"id": 12345},
            "chat": {"id": 12345},
        }
    }

    response = client.post(
        "/api/v1/internal/telegram/webhook",
        headers={"X-Telegram-Bot-Api-Secret-Token": "telegram-webhook-secret"},
        json=payload,
    )

    assert response.status_code == 200
    assert sent and "XEM TRƯỚC" in sent[0]
    with app.state.session_factory() as session:
        assert session.get(AuditEvent, "old-audit") is not None

    actual_payload = dict(payload)
    actual_payload["message"] = dict(payload["message"], message_id=12, text="/cleanup")
    response = client.post(
        "/api/v1/internal/telegram/webhook",
        headers={"X-Telegram-Bot-Api-Secret-Token": "telegram-webhook-secret"},
        json=actual_payload,
    )
    assert response.status_code == 200
    assert len(sent) == 2
    assert "ĐÃ DỌN" in sent[1]
    with app.state.session_factory() as session:
        assert session.get(AuditEvent, "old-audit") is None

    unauthorized = dict(payload)
    unauthorized["message"] = dict(payload["message"], message_id=11, **{"from": {"id": 99999}})
    response = client.post(
        "/api/v1/internal/telegram/webhook",
        headers={"X-Telegram-Bot-Api-Secret-Token": "telegram-webhook-secret"},
        json=unauthorized,
    )
    assert response.status_code == 200
    assert len(sent) == 2
