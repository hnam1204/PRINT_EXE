import os

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")

from datetime import timedelta

from fastapi.testclient import TestClient

from app.database import ActivationCode, utcnow
from app.main import create_app
from app.telegram_bot import create_activation_code


def registration_payload(device_id: str) -> dict:
    return {"deviceId": device_id, "name": device_id, "role": "CLIENT", "appVersion": "1.0.6"}


def test_customer_code_binds_devices_and_reuses_same_device(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'activation-registration.db'}")
    client = TestClient(app)
    with app.state.session_factory() as session:
        code = create_activation_code(session, label="Customer", days=30, max_devices=1, geofence_radius_meters=500, created_by="admin")
        session.commit()

    headers = {"X-Enrollment-Secret": code}
    first = client.post("/api/v1/devices/register", headers=headers, json=registration_payload("client-01"))
    assert first.status_code == 201
    assert first.json()["activationLabel"] == "Customer"
    assert first.json()["activationMaxDevices"] == 1
    assert first.json()["activationUsedDevices"] == 1
    assert first.json()["activationExpiresAt"] is not None
    assert first.json()["activationGeofenceRadiusMeters"] == 500
    profile = client.get("/api/v1/devices/me", headers={"Authorization": f"Bearer {first.json()['deviceToken']}"})
    assert profile.status_code == 200
    assert profile.json()["activationLabel"] == "Customer"
    assert profile.json()["activationMaxDevices"] == 1
    assert profile.json()["activationUsedDevices"] == 1
    assert profile.json()["activationGeofenceRadiusMeters"] == 500
    with app.state.session_factory() as session:
        host_code = create_activation_code(session, label="Host", days=30, max_devices=1, geofence_radius_meters=250, created_by="admin")
        session.commit()
    host = client.post("/api/v1/devices/register", headers={"X-Enrollment-Secret": host_code}, json={**registration_payload("host-01"), "role": "HOST"})
    assert host.status_code == 201
    with app.state.session_factory() as session:
        from app.database import Device
        assert session.get(Device, "host-01").geofence_radius_meters == 250
    second = client.post("/api/v1/devices/register", headers=headers, json=registration_payload("client-01"))
    assert second.status_code == 201

    blocked = client.post("/api/v1/devices/register", headers=headers, json=registration_payload("client-02"))
    assert blocked.status_code == 403
    assert blocked.json()["detail"] == "Activation code device limit reached"


def test_customer_code_rejects_expired_and_revoked_codes(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'activation-expired.db'}")
    client = TestClient(app)
    with app.state.session_factory() as session:
        expired = create_activation_code(session, label="Expired", days=1, max_devices=1, created_by="admin")
        session.flush()
        session.query(ActivationCode).one().expires_at = utcnow() - timedelta(seconds=1)
        session.commit()

    expired_response = client.post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": expired},
        json=registration_payload("expired-device"),
    )
    assert expired_response.status_code == 401

    app = create_app(database_url=f"sqlite:///{tmp_path / 'activation-revoked.db'}")
    client = TestClient(app)
    with app.state.session_factory() as session:
        revoked = create_activation_code(session, label="Revoked", days=30, max_devices=1, created_by="admin")
        session.flush()
        session.query(ActivationCode).one().revoked_at = utcnow()
        session.commit()

    revoked_response = client.post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": revoked},
        json=registration_payload("revoked-device"),
    )
    assert revoked_response.status_code == 401


def test_perpetual_unlimited_code_accepts_multiple_devices(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'activation-unlimited.db'}")
    client = TestClient(app)
    with app.state.session_factory() as session:
        code = create_activation_code(session, label="Enterprise", days=None, max_devices=None, created_by="admin")
        session.commit()

    headers = {"X-Enrollment-Secret": code}
    first = client.post("/api/v1/devices/register", headers=headers, json=registration_payload("enterprise-01"))
    second = client.post("/api/v1/devices/register", headers=headers, json=registration_payload("enterprise-02"))
    assert first.status_code == 201
    assert second.status_code == 201


def test_activation_host_requires_geofence_and_cannot_exceed_radius(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'activation-geofence-policy.db'}")
    client = TestClient(app)
    with app.state.session_factory() as session:
        host_code = create_activation_code(
            session, label="Customer", days=30, max_devices=2,
            geofence_radius_meters=250, created_by="admin",
        )
        session.commit()

    host = client.post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": host_code},
        json={**registration_payload("host-01"), "role": "HOST"},
    )
    assert host.status_code == 201
    host_headers = {"Authorization": f"Bearer {host.json()['deviceToken']}"}

    client_registration = client.post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": "test-enrollment-secret"},
        json=registration_payload("client-01"),
    )
    assert client_registration.status_code == 201
    client_headers = {"Authorization": f"Bearer {client_registration.json()['deviceToken']}"}

    too_large = client.put(
        "/api/v1/devices/geofence",
        headers=host_headers,
        json={"enabled": True, "latitude": 10, "longitude": 106, "radiusMeters": 251},
    )
    assert too_large.status_code == 422

    disabled = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-01"})
    assert disabled.status_code == 503

    configured = client.put(
        "/api/v1/devices/geofence",
        headers=host_headers,
        json={"enabled": True, "latitude": 10, "longitude": 106, "radiusMeters": 250},
    )
    assert configured.status_code == 200

    disable_attempt = client.put(
        "/api/v1/devices/geofence",
        headers=host_headers,
        json={"enabled": False, "radiusMeters": 250},
    )
    assert disable_attempt.status_code == 403

    invalid_radius = client.put(
        "/api/v1/devices/geofence",
        headers=host_headers,
        json={"enabled": True, "latitude": 10, "longitude": 106, "radiusMeters": "NaN"},
    )
    assert invalid_radius.status_code == 422
