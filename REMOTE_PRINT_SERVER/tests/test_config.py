import os

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")

from app.config import Settings


def test_secret_environment_values_are_trimmed(monkeypatch):
    monkeypatch.setenv("ENROLLMENT_SECRET", "  enrollment-secret\r\n")
    monkeypatch.setenv("ADMIN_API_KEY", "  admin-key\t")
    monkeypatch.setenv("CRON_SECRET", " cron-secret ")

    settings = Settings.from_env()

    assert settings.enrollment_secret == "enrollment-secret"
    assert settings.admin_api_key == "admin-key"
    assert settings.cron_secret == "cron-secret"
