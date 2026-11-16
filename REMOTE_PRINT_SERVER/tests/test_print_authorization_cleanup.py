import os
from datetime import timedelta

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")

from app.cleanup import run_cleanup
from app.database import PrintAuthorization, utcnow
from app.main import create_app


def test_cleanup_removes_expired_print_authorizations(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'authorization-cleanup.db'}")
    now = utcnow()
    with app.state.session_factory() as session:
        session.add(PrintAuthorization(
            id="grant-old",
            token_hash="a" * 64,
            job_id="job-1",
            client_id="client-1",
            host_id="host-1",
            printer_id="printer-1",
            expires_at=now - timedelta(seconds=1),
            created_at=now - timedelta(hours=1),
        ))
        session.commit()

    result = run_cleanup(app.state.session_factory, app.state.engine, now=now)
    assert result["status"] == "SUCCEEDED"
    assert result["deleted"]["printAuthorizations"] == 1
