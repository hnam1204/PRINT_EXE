import os

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")

from fastapi.testclient import TestClient

from app.main import create_app


def test_health_does_not_expose_secrets(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    response = TestClient(app).get("/api/v1/health")

    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert "secret" not in str(body).lower()
