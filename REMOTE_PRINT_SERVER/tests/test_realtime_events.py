import os

from fastapi.testclient import TestClient

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")
os.environ.setdefault("REALTIME_GATEWAY_URL", "ws://127.0.0.1:9191/ws")
os.environ.setdefault("REALTIME_GATEWAY_SHARED_SECRET", "realtime-test-secret")

from app.main import create_app


def register(client: TestClient, device_id: str, role: str) -> str:
    response = client.post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": "test-enrollment-secret"},
        json={"deviceId": device_id, "name": device_id, "role": role, "appVersion": "1.0.0"},
    )
    assert response.status_code == 201
    return response.json()["deviceToken"]


def test_pairing_request_is_durable_and_session_is_scoped(tmp_path):
    client = TestClient(create_app(database_url=f"sqlite:///{tmp_path / 'events.db'}"))
    host_token = register(client, "host-01", "HOST")
    client_token = register(client, "client-01", "CLIENT")

    pairing = client.post(
        "/api/v1/pairings",
        headers={"Authorization": f"Bearer {client_token}"},
        json={"hostId": "host-01"},
    )
    assert pairing.status_code == 201

    events = client.get(
        "/api/v1/internal/realtime/events?after=0",
        headers={"X-Realtime-Gateway-Key": "realtime-test-secret"},
    )
    assert events.status_code == 200
    assert events.json()[0]["eventType"] == "PAIRING_REQUESTED"
    assert events.json()[0]["clientId"] == "client-01"
    assert events.json()[0]["hostId"] == "host-01"
    assert events.json()[0]["sequence"] == events.json()[0]["cursor"]

    session = client.post("/api/v1/realtime/session", headers={"Authorization": f"Bearer {host_token}"})
    assert session.status_code == 200
    assert session.json()["gatewayUrl"] == "ws://127.0.0.1:9191/ws"
    assert session.json()["sessionToken"].count(".") == 1


def test_internal_realtime_endpoint_rejects_wrong_gateway_key(tmp_path):
    client = TestClient(create_app(database_url=f"sqlite:///{tmp_path / 'events-auth.db'}"))
    response = client.get(
        "/api/v1/internal/realtime/events",
        headers={"X-Realtime-Gateway-Key": "wrong-key"},
    )
    assert response.status_code == 401
