import os

from fastapi.testclient import TestClient

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")
os.environ.setdefault("REALTIME_GATEWAY_URL", "ws://127.0.0.1:9191/ws")
os.environ.setdefault("REALTIME_GATEWAY_SHARED_SECRET", "realtime-test-secret")

from app.main import create_app
from app.database import Device


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


def test_realtime_authorization_endpoint_tracks_device_revocation(tmp_path):
    client = TestClient(create_app(database_url=f"sqlite:///{tmp_path / 'events-revocation.db'}"))
    token = register(client, "client-revocation", "CLIENT")
    headers = {"X-Realtime-Gateway-Key": "realtime-test-secret"}

    allowed = client.get("/api/v1/internal/realtime/authorize/client-revocation", headers=headers)
    assert allowed.status_code == 200
    assert allowed.json() == {"authorized": True, "role": "CLIENT"}

    profile = client.get("/api/v1/devices/me", headers={"Authorization": f"Bearer {token}"})
    assert profile.status_code == 200
    app = client.app
    with app.state.session_factory() as session:
        device = session.get(Device, "client-revocation")
        device.is_revoked = True
        session.commit()

    revoked = client.get("/api/v1/internal/realtime/authorize/client-revocation", headers=headers)
    assert revoked.status_code == 200
    assert revoked.json() == {"authorized": False}


def test_realtime_pairing_authorization_requires_approved_exact_tuple(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'relay-pairing-auth.db'}")
    client = TestClient(app)
    host_token = register(client, "host-relay-auth", "HOST")
    client_token = register(client, "client-relay-auth", "CLIENT")
    headers = {"X-Realtime-Gateway-Key": "realtime-test-secret"}

    pending = client.post(
        "/api/v1/pairings",
        headers={"Authorization": f"Bearer {client_token}"},
        json={"hostId": "host-relay-auth"},
    ).json()
    pairing_id = pending["pairingId"]

    path = (
        f"/api/v1/internal/realtime/authorize-pairing?host_id=host-relay-auth"
        f"&client_id=client-relay-auth&pairing_id={pairing_id}&device_id=client-relay-auth"
    )
    assert client.get(path, headers=headers).json() == {"authorized": False}

    approved = client.post(
        f"/api/v1/pairings/{pairing_id}/decision",
        headers={"Authorization": f"Bearer {host_token}"},
        json={"approve": True},
    )
    assert approved.status_code == 200

    authorized = client.get(path, headers=headers)
    assert authorized.status_code == 200
    assert authorized.json()["authorized"] is True
    assert authorized.json()["role"] == "CLIENT"
    assert authorized.json()["pairingId"] == pairing_id

    wrong_client = client.get(
        path.replace("client-relay-auth", "other-client"),
        headers=headers,
    )
    assert wrong_client.json() == {"authorized": False}
