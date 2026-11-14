import os

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")

from fastapi.testclient import TestClient

from app.main import create_app
from sqlalchemy.exc import OperationalError


def test_health_does_not_expose_secrets(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    response = TestClient(app).get("/api/v1/health")

    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert body["dependencies"]["database"] == "ready"
    assert "secret" not in str(body).lower()


def test_health_reports_database_unavailable_without_connection_details(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")

    class BrokenEngine:
        def connect(self):
            raise OperationalError("SELECT 1", {}, RuntimeError("connection failed"))

    app.state.engine = BrokenEngine()
    response = TestClient(app).get("/api/v1/health")

    assert response.status_code == 503
    assert response.json()["status"] == "degraded"
    assert response.json()["dependencies"]["database"] == "unavailable"
    assert "connection" not in str(response.json()).lower()
