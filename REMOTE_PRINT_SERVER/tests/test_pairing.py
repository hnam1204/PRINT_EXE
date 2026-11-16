import os

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")
os.environ.setdefault("REALTIME_GATEWAY_SHARED_SECRET", "realtime-test-secret")

from fastapi.testclient import TestClient

from app.database import Device
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

    before_approval = client.get(
        "/api/v1/devices/host-01",
        headers={"Authorization": f"Bearer {client_token}"},
    )
    assert before_approval.status_code == 403

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

    after_approval = client.get(
        "/api/v1/devices/host-01",
        headers={"Authorization": f"Bearer {client_token}"},
    )
    assert after_approval.status_code == 200


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


def test_heartbeat_updates_backend_app_version(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'heartbeat-version.db'}")
    client = TestClient(app)
    token = register(client, "heartbeat-version-device", "HOST")

    response = client.post(
        "/api/v1/devices/heartbeat",
        headers={"Authorization": f"Bearer {token}"},
        json={"appVersion": "1.0.27"},
    )

    assert response.status_code == 200
    with app.state.session_factory() as session:
        device = session.get(Device, "heartbeat-version-device")
        assert device.app_version == "1.0.27"


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


def test_client_reuses_existing_approved_pairing_instead_of_creating_duplicate(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'approved-retry.db'}")
    client = TestClient(app)
    host_token = register(client, "host-approved", "HOST")
    client_token = register(client, "client-approved", "CLIENT")
    host_headers = {"Authorization": f"Bearer {host_token}"}
    client_headers = {"Authorization": f"Bearer {client_token}"}

    first = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-approved"})
    assert first.status_code == 201
    pairing_id = first.json()["pairingId"]
    approved = client.post(
        f"/api/v1/pairings/{pairing_id}/decision",
        headers=host_headers,
        json={"approve": True},
    )
    assert approved.status_code == 200

    retry = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-approved"})

    assert retry.status_code == 201
    assert retry.json()["pairingId"] == pairing_id
    assert retry.json()["status"] == "APPROVED"


def test_expired_pending_pairing_is_closed_before_new_request(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'expired-retry.db'}")
    client = TestClient(app)
    register(client, "host-expired", "HOST")
    client_token = register(client, "client-expired", "CLIENT")
    client_headers = {"Authorization": f"Bearer {client_token}"}

    first = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-expired"})
    assert first.status_code == 201

    from datetime import timedelta
    from app.database import Pairing, utcnow

    with app.state.session_factory() as session:
        pairing = session.get(Pairing, first.json()["pairingId"])
        pairing.expires_at = utcnow() - timedelta(seconds=1)
        session.commit()

    second = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-expired"})

    assert second.status_code == 201
    assert second.json()["pairingId"] != first.json()["pairingId"]

    events = client.get(
        "/api/v1/internal/realtime/events?after=0",
        headers={"X-Realtime-Gateway-Key": "realtime-test-secret"},
    )
    assert events.status_code == 200
    expiry_events = [
        event for event in events.json()
        if event["eventType"] == "PAIRING_REVOKED"
        and event["pairingId"] == first.json()["pairingId"]
    ]
    assert len(expiry_events) == 1
    assert expiry_events[0]["recipientDeviceId"] == "client-expired"


def test_approved_pairing_printer_permissions_are_explicit_and_update_in_place(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'printer-permissions.db'}")
    client = TestClient(app)
    host_token = register(client, "host-printer-permissions", "HOST")
    client_token = register(client, "client-printer-permissions", "CLIENT")
    other_host_token = register(client, "other-host-printer-permissions", "HOST")
    host_headers = {"Authorization": f"Bearer {host_token}"}
    client_headers = {"Authorization": f"Bearer {client_token}"}
    other_host_headers = {"Authorization": f"Bearer {other_host_token}"}

    for printer_id, name in (("printer-a", "A"), ("printer-b", "B")):
        response = client.post(
            "/api/v1/printers",
            headers=host_headers,
            json={"printerId": printer_id, "displayName": name, "isShared": True},
        )
        assert response.status_code == 200

    pairing = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-printer-permissions"}).json()
    approved = client.post(
        f"/api/v1/pairings/{pairing['pairingId']}/decision",
        headers=host_headers,
        json={"approve": True, "printerAccessMode": "SELECTED_ONLY", "allowedPrinters": ["printer-a"]},
    )
    assert approved.status_code == 200
    assert approved.json()["printerAccessMode"] == "SELECTED_ONLY"

    visible = client.get("/api/v1/printers?hostId=host-printer-permissions", headers=client_headers)
    assert [item["printerId"] for item in visible.json()] == ["printer-a"]

    updated = client.put(
        f"/api/v1/pairings/{pairing['pairingId']}/printer-permissions",
        headers=host_headers,
        json={"printerAccessMode": "NONE", "allowedPrinters": []},
    )
    assert updated.status_code == 200
    assert updated.json()["printerAccessMode"] == "NONE"
    assert client.get("/api/v1/printers?hostId=host-printer-permissions", headers=client_headers).json() == []

    forbidden_client = client.put(
        f"/api/v1/pairings/{pairing['pairingId']}/printer-permissions",
        headers=client_headers,
        json={"printerAccessMode": "ALL_SHARED", "allowedPrinters": []},
    )
    assert forbidden_client.status_code == 403
    forbidden_host = client.put(
        f"/api/v1/pairings/{pairing['pairingId']}/printer-permissions",
        headers=other_host_headers,
        json={"printerAccessMode": "ALL_SHARED", "allowedPrinters": []},
    )
    assert forbidden_host.status_code == 404


def test_unshared_printer_is_not_granted_by_selected_permission(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'unshared-permissions.db'}")
    client = TestClient(app)
    host_token = register(client, "host-unshared", "HOST")
    client_token = register(client, "client-unshared", "CLIENT")
    host_headers = {"Authorization": f"Bearer {host_token}"}
    client_headers = {"Authorization": f"Bearer {client_token}"}

    created = client.post(
        "/api/v1/printers",
        headers=host_headers,
        json={"printerId": "private-printer", "displayName": "Private", "isShared": False},
    )
    assert created.status_code == 200
    pairing = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-unshared"}).json()
    rejected = client.post(
        f"/api/v1/pairings/{pairing['pairingId']}/decision",
        headers=host_headers,
        json={"approve": True, "printerAccessMode": "SELECTED_ONLY", "allowedPrinters": ["private-printer"]},
    )
    assert rejected.status_code == 409


def test_two_clients_receive_only_their_own_selected_printers(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'isolated-printers.db'}")
    client = TestClient(app)
    host_token = register(client, "host-isolated", "HOST")
    client_a_token = register(client, "client-a-isolated", "CLIENT")
    client_b_token = register(client, "client-b-isolated", "CLIENT")
    host_headers = {"Authorization": f"Bearer {host_token}"}

    for printer_id in ("printer-a-isolated", "printer-b-isolated"):
        assert client.post(
            "/api/v1/printers",
            headers=host_headers,
            json={"printerId": printer_id, "displayName": printer_id, "isShared": True},
        ).status_code == 200

    pairings = []
    for token in (client_a_token, client_b_token):
        pairing = client.post(
            "/api/v1/pairings",
            headers={"Authorization": f"Bearer {token}"},
            json={"hostId": "host-isolated"},
        ).json()
        pairings.append(pairing["pairingId"])

    for pairing_id, printer_id in zip(pairings, ("printer-a-isolated", "printer-b-isolated")):
        approved = client.post(
            f"/api/v1/pairings/{pairing_id}/decision",
            headers=host_headers,
            json={"approve": True, "printerAccessMode": "SELECTED_ONLY", "allowedPrinters": [printer_id]},
        )
        assert approved.status_code == 200

    visible_a = client.get(
        "/api/v1/printers?hostId=host-isolated",
        headers={"Authorization": f"Bearer {client_a_token}"},
    )
    visible_b = client.get(
        "/api/v1/printers?hostId=host-isolated",
        headers={"Authorization": f"Bearer {client_b_token}"},
    )
    assert [item["printerId"] for item in visible_a.json()] == ["printer-a-isolated"]
    assert [item["printerId"] for item in visible_b.json()] == ["printer-b-isolated"]


def test_host_can_resolve_an_existing_approved_pairing_after_restart(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'approved-lookup.db'}")
    client = TestClient(app)
    host_token = register(client, "host-approved-lookup", "HOST")
    client_token = register(client, "client-approved-lookup", "CLIENT")
    host_headers = {"Authorization": f"Bearer {host_token}"}
    client_headers = {"Authorization": f"Bearer {client_token}"}
    pairing = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-approved-lookup"}).json()
    approved = client.post(
        f"/api/v1/pairings/{pairing['pairingId']}/decision",
        headers=host_headers,
        json={"approve": True},
    )
    assert approved.status_code == 200

    resolved = client.get(
        "/api/v1/pairings/approved/client-approved-lookup",
        headers=host_headers,
    )
    assert resolved.status_code == 200
    assert resolved.json()["pairingId"] == pairing["pairingId"]
