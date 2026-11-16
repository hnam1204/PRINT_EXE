import os

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")

from fastapi.testclient import TestClient

from app.main import create_app


def test_device_registration_returns_token_once_and_heartbeat(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    client = TestClient(app)
    response = client.post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": "test-enrollment-secret"},
        json={"deviceId": "client-01", "name": "Office Client", "role": "CLIENT", "appVersion": "1.0.0"},
    )

    assert response.status_code == 201
    body = response.json()
    assert body["deviceId"] == "client-01"
    assert body["deviceToken"]
    assert body["isActivationCode"] is False

    profile = client.get("/api/v1/devices/me", headers={"Authorization": f"Bearer {body['deviceToken']}"})
    assert profile.status_code == 200
    assert profile.json()["role"] == "CLIENT"

    heartbeat = client.post("/api/v1/devices/heartbeat", headers={"Authorization": f"Bearer {body['deviceToken']}"})
    assert heartbeat.status_code == 200
    assert heartbeat.json()["status"] == "ONLINE"


def test_registration_rejects_wrong_secret(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    response = TestClient(app).post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": "wrong"},
        json={"deviceId": "client-01", "name": "Office Client", "role": "CLIENT", "appVersion": "1.0.0"},
    )
    assert response.status_code == 401
