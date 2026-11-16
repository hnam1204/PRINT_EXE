import os
import re

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


def test_client_cannot_manage_host_pairing_codes(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    client = TestClient(app)
    client_token = register(client, "client-01", "CLIENT")
    headers = {"Authorization": f"Bearer {client_token}"}

    issue = client.post(
        "/api/v1/pairing-codes/issue",
        headers=headers,
        json={"sessionId": "client-session"},
    )
    current = client.get("/api/v1/pairing-codes/current", headers=headers)
    revoke = client.post(
        "/api/v1/pairing-codes/revoke",
        headers=headers,
        json={"sessionId": "client-session"},
    )

    assert issue.status_code == 403
    assert current.status_code == 403
    assert revoke.status_code == 403


def test_code_is_six_chars_unique_and_reissued_for_new_session(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    client = TestClient(app)
    host_token = register(client, "host-01", "HOST")
    headers = {"Authorization": f"Bearer {host_token}"}
    codes = set()
    latest_code = None

    for index in range(1000):
        response = client.post("/api/v1/pairing-codes/issue", headers=headers, json={"sessionId": f"boot-{index}"})
        assert response.status_code == 201
        code = response.json()["code"]
        assert len(code) == 6
        assert re.fullmatch(r"[A-Z0-9]{6}", code)
        assert code not in codes
        codes.add(code)
        latest_code = code

    same_session = client.post("/api/v1/pairing-codes/issue", headers=headers, json={"sessionId": "boot-999"})
    assert same_session.status_code == 201
    assert same_session.json()["code"] == latest_code

    current = client.get("/api/v1/pairing-codes/current", headers=headers)
    assert current.status_code == 200
    assert current.json()["code"] == same_session.json()["code"]


def test_resolve_code_requires_client_and_host_approval(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    client = TestClient(app)
    host_token = register(client, "host-01", "HOST")
    client_token = register(client, "client-01", "CLIENT")
    issued = client.post(
        "/api/v1/pairing-codes/issue",
        headers={"Authorization": f"Bearer {host_token}"},
        json={"sessionId": "boot-1"},
    ).json()
    resolved = client.post(
        "/api/v1/pairing-codes/resolve",
        headers={"Authorization": f"Bearer {client_token}"},
        json={"code": issued["code"]},
    )
    assert resolved.status_code == 201
    assert resolved.json()["status"] == "PENDING"

    revoked = client.post(
        "/api/v1/pairing-codes/revoke",
        headers={"Authorization": f"Bearer {host_token}"},
        json={"sessionId": "boot-1"},
    )
    assert revoked.status_code == 200
    assert revoked.json()["status"] == "REVOKED"


def test_repeated_resolve_returns_same_live_pairing(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'pairing-retry.db'}")
    client = TestClient(app)
    host_token = register(client, "host-01", "HOST")
    client_token = register(client, "client-01", "CLIENT")
    issued = client.post(
        "/api/v1/pairing-codes/issue",
        headers={"Authorization": f"Bearer {host_token}"},
        json={"sessionId": "boot-1"},
    ).json()
    headers = {"Authorization": f"Bearer {client_token}"}

    first = client.post("/api/v1/pairing-codes/resolve", headers=headers, json={"code": issued["code"]})
    second = client.post("/api/v1/pairing-codes/resolve", headers=headers, json={"code": issued["code"]})

    assert first.status_code == 201
    assert second.status_code == 201
    assert second.json()["pairingId"] == first.json()["pairingId"]


def test_rotating_code_revokes_existing_pairing(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    client = TestClient(app)
    host_token = register(client, "host-01", "HOST")
    client_token = register(client, "client-01", "CLIENT")
    host_headers = {"Authorization": f"Bearer {host_token}"}
    client_headers = {"Authorization": f"Bearer {client_token}"}

    issued = client.post(
        "/api/v1/pairing-codes/issue",
        headers=host_headers,
        json={"sessionId": "boot-1"},
    ).json()
    resolved = client.post(
        "/api/v1/pairing-codes/resolve",
        headers=client_headers,
        json={"code": issued["code"]},
    )
    assert resolved.status_code == 201
    pairing_id = resolved.json()["pairingId"]
    approved = client.post(
        f"/api/v1/pairings/{pairing_id}/decision",
        headers=host_headers,
        json={"approve": True},
    )
    assert approved.status_code == 200

    rotated = client.post(
        "/api/v1/pairing-codes/issue",
        headers=host_headers,
        json={"sessionId": "boot-2"},
    )
    assert rotated.status_code == 201
    assert rotated.json()["code"] != issued["code"]

    old_pairing = client.get(
        f"/api/v1/pairings/{pairing_id}",
        headers=client_headers,
    )
    assert old_pairing.status_code == 200
    assert old_pairing.json()["status"] == "REVOKED"

    old_code = client.post(
        "/api/v1/pairing-codes/resolve",
        headers=client_headers,
        json={"code": issued["code"]},
    )
    assert old_code.status_code == 404


def test_database_collision_causes_secure_retry(tmp_path, monkeypatch):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    client = TestClient(app)
    token = register(client, "host-01", "HOST")
    headers = {"Authorization": f"Bearer {token}"}
    first = client.post("/api/v1/pairing-codes/issue", headers=headers, json={"sessionId": "boot-1"})
    assert first.status_code == 201

    sequence = iter([first.json()["code"], "ZZZZZ9"])
    monkeypatch.setattr("app.pairing_codes.random_pairing_code", lambda: next(sequence))
    second = client.post("/api/v1/pairing-codes/issue", headers=headers, json={"sessionId": "boot-2"})

    assert second.status_code == 201
    assert second.json()["code"] == "ZZZZZ9"
