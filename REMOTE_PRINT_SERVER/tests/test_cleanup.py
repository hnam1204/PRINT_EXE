import os
from datetime import timedelta

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")
os.environ.setdefault("CRON_SECRET", "test-cron-secret")

from fastapi.testclient import TestClient

from app.cleanup import run_cleanup
from app.database import AuditEvent, Device, Pairing, PairingCode, PrintJob, Printer, RateLimitEvent, utcnow
from app.main import create_app


def test_cleanup_deletes_only_safe_terminal_data(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    old = utcnow() - timedelta(hours=73)
    recent = utcnow() - timedelta(hours=1)
    with app.state.session_factory() as session:
        session.add_all([
            Device(id="host-01", name="Host", role="HOST", app_version="1", token_hash="host", last_seen_at=old, created_at=old),
            Device(id="client-01", name="Client", role="CLIENT", app_version="1", token_hash="client", last_seen_at=old, created_at=old),
            Pairing(id="old-rejected", host_id="host-01", client_id="client-01", code="OLD", status="REJECTED", expires_at=old, created_at=old),
            Pairing(id="approved", host_id="host-01", client_id="client-01", code="KEEP", status="APPROVED", expires_at=old, created_at=old),
            Printer(id="printer-01", host_id="host-01", display_name="Printer", updated_at=old),
            PrintJob(id="done-old", idempotency_key="done-old", client_id="client-01", host_id="host-01", printer_id="printer-01", status="COMPLETED", request_hash="a", created_at=old, updated_at=old),
            PrintJob(id="queued-old", idempotency_key="queued-old", client_id="client-01", host_id="host-01", printer_id="printer-01", status="QUEUED", request_hash="b", created_at=old, updated_at=old),
            PrintJob(id="done-recent", idempotency_key="done-recent", client_id="client-01", host_id="host-01", printer_id="printer-01", status="COMPLETED", request_hash="c", created_at=recent, updated_at=recent),
            AuditEvent(id="audit-old", action="TEST", target_type="TEST", created_at=old),
            RateLimitEvent(id="rate-old", bucket="test", created_at=old),
        ])
        session.commit()

    result = run_cleanup(app.state.session_factory, app.state.engine, now=utcnow())
    assert result["status"] == "SUCCEEDED"
    assert result["deleted"]["pairings"] == 1
    assert result["deleted"]["printJobs"] == 1

    with app.state.session_factory() as session:
        assert session.get(Device, "host-01") is not None
        assert session.get(Pairing, "approved") is not None
        assert session.get(Pairing, "old-rejected") is None
        assert session.get(PrintJob, "done-old") is None
        assert session.get(PrintJob, "queued-old") is not None
        assert session.get(PrintJob, "done-recent") is not None
        assert session.get(Printer, "printer-01") is not None
        assert session.get(AuditEvent, "audit-old") is None
        assert session.get(RateLimitEvent, "rate-old") is None


def test_cleanup_endpoint_requires_cron_secret(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    client = TestClient(app)
    assert client.get("/api/v1/internal/cleanup").status_code == 401
    assert client.get("/api/v1/internal/cleanup", headers={"Authorization": "Bearer wrong"}).status_code == 401
    assert client.get("/api/v1/internal/cleanup", headers={"Authorization": "Bearer test-cron-secret"}).status_code == 200
