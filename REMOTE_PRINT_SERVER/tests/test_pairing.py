import os

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")

from fastapi.testclient import TestClient

from app.main import create_app


def register(client, device_id, role):
    response = client.post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": "test-enrollment-secret"},
        json={"deviceId": device_id, "name": device_id, "role": role, "appVersion": "1.0.0"},
    )
    assert response.status_code == 201
    return response.json()["deviceToken"]


def test_client_requests_and_host_approves_pairing(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    client = TestClient(app)
    host_token = register(client, "host-01", "HOST")
    client_token = register(client, "client-01", "CLIENT")

    request = client.post(
        "/api/v1/pairings",
        headers={"Authorization": f"Bearer {client_token}"},
        json={"hostId": "host-01"},
    )
    assert request.status_code == 201
    pairing = request.json()
    assert len(pairing["code"]) == 6

    pending = client.get("/api/v1/pairings/pending", headers={"Authorization": f"Bearer {host_token}"})
    assert pending.status_code == 200
    assert pending.json()[0]["pairingId"] == pairing["pairingId"]

    decision = client.post(
        f"/api/v1/pairings/{pairing['pairingId']}/decision",
        headers={"Authorization": f"Bearer {host_token}"},
        json={"approve": True},
    )
    assert decision.status_code == 200
    assert decision.json()["status"] == "APPROVED"

    status = client.get(
        f"/api/v1/pairings/{pairing['pairingId']}",
        headers={"Authorization": f"Bearer {client_token}"},
    )
    assert status.status_code == 200
    assert status.json()["status"] == "APPROVED"


def test_host_geofence_route_is_not_captured_by_device_id_route(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'geofence-route.db'}")
    client = TestClient(app)
    host_token = register(client, "host-geofence", "HOST")

    response = client.get(
        "/api/v1/devices/geofence",
        headers={"Authorization": f"Bearer {host_token}"},
    )

    assert response.status_code == 200
    assert response.json()["hostId"] == "host-geofence"


def test_rejected_client_can_create_a_new_pending_pairing_without_approving_it(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'reconnect.db'}")
    client = TestClient(app)
    host_token = register(client, "host-reconnect", "HOST")
    client_token = register(client, "client-reconnect", "CLIENT")
    headers = {"Authorization": f"Bearer {client_token}"}
    host_headers = {"Authorization": f"Bearer {host_token}"}

    first = client.post("/api/v1/pairings", headers=headers, json={"hostId": "host-reconnect"}).json()
    rejected = client.post(
        f"/api/v1/pairings/{first['pairingId']}/decision",
        headers=host_headers,
        json={"approve": False},
    )
    assert rejected.status_code == 200
    assert rejected.json()["status"] == "REJECTED"

    second = client.post("/api/v1/pairings", headers=headers, json={"hostId": "host-reconnect"})
    assert second.status_code == 201
    assert second.json()["status"] == "PENDING"
    assert second.json()["pairingId"] != first["pairingId"]

    old = client.get(
        f"/api/v1/pairings/{first['pairingId']}",
        headers=headers,
    )
    assert old.status_code == 200
    assert old.json()["status"] == "REJECTED"
