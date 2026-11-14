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


def test_shared_printer_and_idempotent_job(tmp_path):
    app = create_app(database_url=f"sqlite:///{tmp_path / 'test.db'}")
    client = TestClient(app)
    host = register(client, "host-01", "HOST")
    client_token = register(client, "client-01", "CLIENT")
    host_headers = {"Authorization": f"Bearer {host}"}
    client_headers = {"Authorization": f"Bearer {client_token}"}

    printer = client.post(
        "/api/v1/printers", headers=host_headers,
        json={"printerId": "printer-01", "displayName": "Office Printer", "isShared": True},
    )
    assert printer.status_code == 200

    pairing = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-01"}).json()
    client.post(f"/api/v1/pairings/{pairing['pairingId']}/decision", headers=host_headers, json={"approve": True})

    printers = client.get("/api/v1/printers?hostId=host-01", headers=client_headers)
    assert printers.status_code == 200
    assert printers.json()[0]["printerId"] == "printer-01"

    job_headers = {**client_headers, "Idempotency-Key": "job-001"}
    payload = {"hostId": "host-01", "printerId": "printer-01", "metadata": {"copies": 1}}
    first = client.post("/api/v1/jobs", headers=job_headers, json=payload)
    second = client.post("/api/v1/jobs", headers=job_headers, json=payload)
    assert first.status_code == 201
    assert second.status_code == 201
    assert first.json()["jobId"] == second.json()["jobId"]
