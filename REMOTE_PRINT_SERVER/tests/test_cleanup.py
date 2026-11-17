import os
from datetime import datetime, timedelta, timezone

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")
os.environ.setdefault("CRON_SECRET", "test-cron-secret")

from fastapi.testclient import TestClient

from app.cleanup import run_cleanup
from app.database import (
    AuditEvent,
    Device,
    Pairing,
    PairingCode,
    PrintJob,
    PrintJobTombstone,
    Printer,
    RateLimitEvent,
    RealtimeEvent,
    TelegramMessage,
    utcnow,
)
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
            Pairing(id="old-revoked", host_id="host-01", client_id="client-01", code="OLD-REVOKED", status="REVOKED", expires_at=old, created_at=old, decided_at=old),
            Pairing(id="recent-revoked", host_id="host-01", client_id="client-01", code="RECENT-REVOKED", status="REVOKED", expires_at=old, created_at=old, decided_at=recent),
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
    assert result["deleted"]["pairings"] == 2
    assert result["deleted"]["printJobs"] == 1

    with app.state.session_factory() as session:
        assert session.get(Device, "host-01") is not None
        assert session.get(Pairing, "approved") is not None
        assert session.get(Pairing, "old-rejected") is None
        assert session.get(Pairing, "old-revoked") is None
        assert session.get(Pairing, "recent-revoked") is not None
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


class FailingBlobStore:
    def delete(self, reference):
        raise RuntimeError("staging blob unavailable")


def test_cleanup_uses_exact_72_hour_boundary_and_preserves_uncertain_jobs(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'boundary.db'}")
    now = datetime(2026, 1, 1, tzinfo=timezone.utc)
    old = now - timedelta(hours=72)
    recent = now - timedelta(hours=71)
    future_lease = now + timedelta(minutes=10)
    with app.state.session_factory() as session:
        session.add_all([
            Device(id="host-01", name="Host", role="HOST", app_version="1", token_hash="host", last_seen_at=now, created_at=now),
            Device(id="client-01", name="Client", role="CLIENT", app_version="1", token_hash="client", last_seen_at=now, created_at=now),
            Printer(id="printer-01", host_id="host-01", display_name="Printer", updated_at=now),
            PrintJob(id="exact-72", idempotency_key="exact-72", client_id="client-01", host_id="host-01", printer_id="printer-01", status="COMPLETED", request_hash="a", created_at=old, updated_at=old),
            PrintJob(id="age-71", idempotency_key="age-71", client_id="client-01", host_id="host-01", printer_id="printer-01", status="FAILED", request_hash="b", created_at=recent, updated_at=recent),
            PrintJob(id="unknown", idempotency_key="unknown", client_id="client-01", host_id="host-01", printer_id="printer-01", status="UNKNOWN", request_hash="c", created_at=old, updated_at=old),
            PrintJob(id="leased", idempotency_key="leased", client_id="client-01", host_id="host-01", printer_id="printer-01", status="COMPLETED", request_hash="d", created_at=old, updated_at=old, claim_expires_at=future_lease),
        ])
        session.commit()

    result = run_cleanup(app.state.session_factory, app.state.engine, now=now)
    assert result["status"] == "SUCCEEDED"
    assert result["deleted"]["printJobs"] == 1
    with app.state.session_factory() as session:
        assert session.get(PrintJob, "exact-72") is None
        assert session.query(PrintJobTombstone).filter_by(idempotency_key="exact-72").one() is not None
        assert session.get(PrintJob, "age-71") is not None
        assert session.get(PrintJob, "unknown") is not None
        assert session.get(PrintJob, "leased") is not None


def test_blob_failure_retains_job_and_marks_partial_failure(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'blob-failure.db'}")
    old = utcnow() - timedelta(hours=73)
    with app.state.session_factory() as session:
        session.add_all([
            Device(id="host-01", name="Host", role="HOST", app_version="1", token_hash="host", last_seen_at=old, created_at=old),
            Device(id="client-01", name="Client", role="CLIENT", app_version="1", token_hash="client", last_seen_at=old, created_at=old),
            Printer(id="printer-01", host_id="host-01", display_name="Printer", updated_at=old),
            PrintJob(id="blob-job", idempotency_key="blob-job", client_id="client-01", host_id="host-01", printer_id="printer-01", status="COMPLETED", request_hash="a", data_ref="jobs/blob", created_at=old, updated_at=old),
        ])
        session.commit()

    result = run_cleanup(app.state.session_factory, app.state.engine, FailingBlobStore(), now=utcnow())
    assert result["status"] == "PARTIAL_FAILED"
    assert result["retained"]["blobFailures"] == 1
    with app.state.session_factory() as session:
        assert session.get(PrintJob, "blob-job") is not None


def test_cleanup_dry_run_does_not_delete_or_create_tombstone(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'dry-run.db'}")
    old = utcnow() - timedelta(hours=73)
    with app.state.session_factory() as session:
        session.add_all([
            Device(id="host-01", name="Host", role="HOST", app_version="1", token_hash="host", last_seen_at=old, created_at=old),
            Device(id="client-01", name="Client", role="CLIENT", app_version="1", token_hash="client", last_seen_at=old, created_at=old),
            Printer(id="printer-01", host_id="host-01", display_name="Printer", updated_at=old),
            PrintJob(id="dry-job", idempotency_key="dry-job", client_id="client-01", host_id="host-01", printer_id="printer-01", status="COMPLETED", request_hash="a", created_at=old, updated_at=old),
        ])
        session.commit()

    result = run_cleanup(app.state.session_factory, app.state.engine, now=utcnow(), dry_run=True)
    assert result["dryRun"] is True
    assert result["deleted"]["printJobs"] == 0
    with app.state.session_factory() as session:
        assert session.get(PrintJob, "dry-job") is not None


def test_cleanup_removes_expired_retention_records(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'retention.db'}")
    now = datetime(2026, 1, 31, tzinfo=timezone.utc)
    old = now - timedelta(days=31)
    recent = now - timedelta(days=1)
    with app.state.session_factory() as session:
        session.add_all([
            Device(id="host-01", name="Host", role="HOST", app_version="1", token_hash="host", last_seen_at=now, created_at=now),
            Device(id="client-01", name="Client", role="CLIENT", app_version="1", token_hash="client", last_seen_at=now, created_at=now),
            PairingCode(
                id="old-code", session_id="old-session", device_id="host-01", code_hash="old-hash",
                code_ciphertext="old-ciphertext", issued_at=old, expires_at=old, status="EXPIRED",
            ),
            PairingCode(
                id="recent-code", session_id="recent-session", device_id="host-01", code_hash="recent-hash",
                code_ciphertext="recent-ciphertext", issued_at=recent, expires_at=recent, status="EXPIRED",
            ),
            RealtimeEvent(id=1, event_type="OLD", recipient_device_id="client-01", created_at=old),
            RealtimeEvent(id=2, event_type="RECENT", recipient_device_id="client-01", created_at=recent),
            TelegramMessage(id="old-message", chat_id="123", message_id=1, direction="IN", created_at=old),
            TelegramMessage(id="recent-message", chat_id="123", message_id=2, direction="IN", created_at=recent),
            PrintJobTombstone(
                id="old-tombstone", idempotency_key="old-key", request_hash="old-request",
                original_job_id="old-job", created_at=old, expires_at=None,
            ),
            PrintJobTombstone(
                id="recent-tombstone", idempotency_key="recent-key", request_hash="recent-request",
                original_job_id="recent-job", created_at=recent, expires_at=None,
            ),
        ])
        session.commit()

    result = run_cleanup(app.state.session_factory, app.state.engine, now=now)

    assert result["status"] == "SUCCEEDED"
    assert result["deleted"]["pairingCodes"] == 1
    assert result["deleted"]["realtimeEvents"] == 1
    assert result["deleted"]["telegramMessages"] == 1
    assert result["deleted"]["printJobTombstones"] == 1
    with app.state.session_factory() as session:
        assert session.get(PairingCode, "old-code") is None
        assert session.get(PairingCode, "recent-code") is not None
        assert session.get(RealtimeEvent, 1) is None
        assert session.get(RealtimeEvent, 2) is not None
        assert session.get(TelegramMessage, "old-message") is None
        assert session.get(TelegramMessage, "recent-message") is not None
        assert session.get(PrintJobTombstone, "old-tombstone") is None
        assert session.get(PrintJobTombstone, "recent-tombstone") is not None
