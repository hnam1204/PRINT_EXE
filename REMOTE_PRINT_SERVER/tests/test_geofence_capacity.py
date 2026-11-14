from datetime import datetime, timedelta, timezone
import os
from dataclasses import replace

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")

from app.geofence import verify_location
from fastapi.testclient import TestClient

from app.main import create_app
from app.config import Settings


def make_test_app(database_url):
    settings = replace(Settings.from_env(), rate_limit_max_attempts=100)
    return create_app(settings=settings, database_url=database_url)


def register(client, device_id, role):
    response = client.post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": "test-enrollment-secret"},
        json={"deviceId": device_id, "name": device_id, "role": role, "appVersion": "1.0.0"},
    )
    assert response.status_code == 201
    return response.json()["deviceToken"]


def test_location_is_verified_only_when_accuracy_proves_it_is_inside_radius():
    now = datetime.now(timezone.utc)
    result = verify_location(
        host_latitude=10.0,
        host_longitude=106.0,
        radius_meters=1000,
        client_latitude=10.0,
        client_longitude=106.0,
        accuracy_meters=50,
        location_timestamp=now,
        now=now,
    )
    assert result.status == "VERIFIED"


def test_location_near_boundary_is_unverified_when_error_overlaps_boundary():
    now = datetime.now(timezone.utc)
    result = verify_location(
        host_latitude=10.0,
        host_longitude=106.0,
        radius_meters=1000,
        client_latitude=10.009,
        client_longitude=106.0,
        accuracy_meters=200,
        location_timestamp=now,
        now=now,
    )
    assert result.status == "UNVERIFIED"


def test_stale_location_is_not_accepted():
    now = datetime.now(timezone.utc)
    result = verify_location(
        host_latitude=10.0,
        host_longitude=106.0,
        radius_meters=1000,
        client_latitude=10.0,
        client_longitude=106.0,
        accuracy_meters=50,
        location_timestamp=now - timedelta(minutes=6),
        now=now,
    )
    assert result.status == "UNVERIFIED"


def test_host_approves_at_most_fifteen_distinct_clients(tmp_path):
    app = make_test_app(f"sqlite:///{tmp_path / 'capacity.db'}")
    client = TestClient(app)
    host_token = register(client, "host-01", "HOST")
    host_headers = {"Authorization": f"Bearer {host_token}"}

    pairings = []
    for index in range(16):
        client_token = register(client, f"client-{index:02d}", "CLIENT")
        request = client.post(
            "/api/v1/pairings",
            headers={"Authorization": f"Bearer {client_token}"},
            json={"hostId": "host-01"},
        )
        assert request.status_code == 201
        pairings.append(request.json()["pairingId"])

    for pairing_id in pairings[:15]:
        approved = client.post(f"/api/v1/pairings/{pairing_id}/decision", headers=host_headers, json={"approve": True})
        assert approved.status_code == 200

    rejected = client.post(f"/api/v1/pairings/{pairings[15]}/decision", headers=host_headers, json={"approve": True})
    assert rejected.status_code == 409


def test_revoke_releases_capacity_without_deleting_device_or_history(tmp_path):
    app = make_test_app(f"sqlite:///{tmp_path / 'revoke.db'}")
    client = TestClient(app)
    host_token = register(client, "host-01", "HOST")
    host_headers = {"Authorization": f"Bearer {host_token}"}
    first_client_token = register(client, "client-01", "CLIENT")
    second_client_token = register(client, "client-02", "CLIENT")

    first = client.post("/api/v1/pairings", headers={"Authorization": f"Bearer {first_client_token}"}, json={"hostId": "host-01"}).json()
    second = client.post("/api/v1/pairings", headers={"Authorization": f"Bearer {second_client_token}"}, json={"hostId": "host-01"}).json()
    assert client.post(f"/api/v1/pairings/{first['pairingId']}/decision", headers=host_headers, json={"approve": True}).status_code == 200
    revoked = client.post(f"/api/v1/pairings/{first['pairingId']}/revoke", headers=host_headers)
    assert revoked.status_code == 200
    assert revoked.json()["status"] == "REVOKED"
    assert client.get("/api/v1/devices/me", headers={"Authorization": f"Bearer {first_client_token}"}).status_code == 200
    assert client.post(f"/api/v1/pairings/{second['pairingId']}/decision", headers=host_headers, json={"approve": True}).status_code == 200


def test_enabled_geofence_rejects_missing_location_and_accepts_verified_location(tmp_path):
    app = make_test_app(f"sqlite:///{tmp_path / 'geofence.db'}")
    client = TestClient(app)
    host_token = register(client, "host-01", "HOST")
    client_token = register(client, "client-01", "CLIENT")
    host_headers = {"Authorization": f"Bearer {host_token}"}
    client_headers = {"Authorization": f"Bearer {client_token}"}

    configured = client.put(
        "/api/v1/devices/geofence",
        headers=host_headers,
        json={"enabled": True, "latitude": 10.0, "longitude": 106.0, "radiusMeters": 1000},
    )
    assert configured.status_code == 200

    missing = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-01"})
    assert missing.status_code == 403

    verified = client.post(
        "/api/v1/pairings",
        headers=client_headers,
        json={
            "hostId": "host-01",
            "latitude": 10.0,
            "longitude": 106.0,
            "accuracyMeters": 50,
            "locationTimestamp": datetime.now(timezone.utc).isoformat(),
        },
    )
    assert verified.status_code == 201
    assert verified.json()["locationStatus"] == "VERIFIED"
