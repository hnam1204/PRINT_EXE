import os

from fastapi.testclient import TestClient

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")

from app.main import create_app


def register(client: TestClient, device_id: str, role: str) -> str:
    response = client.post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": "test-enrollment-secret"},
        json={"deviceId": device_id, "name": device_id, "role": role, "appVersion": "1.0.0"},
    )
    assert response.status_code == 201
    return response.json()["deviceToken"]


def setup_job(tmp_path):
    client = TestClient(create_app(database_url=f"sqlite:///{tmp_path / 'jobs.db'}"))
    host_token = register(client, "host-01", "HOST")
    client_token = register(client, "client-01", "CLIENT")
    host_headers = {"Authorization": f"Bearer {host_token}"}
    client_headers = {"Authorization": f"Bearer {client_token}"}
    assert client.post(
        "/api/v1/printers",
        headers=host_headers,
        json={"printerId": "printer-01", "displayName": "Office Printer", "isShared": True},
    ).status_code == 200
    pairing = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-01"})
    assert pairing.status_code == 201
    assert client.post(
        f"/api/v1/pairings/{pairing.json()['pairingId']}/decision",
        headers=host_headers,
        json={"approve": True},
    ).status_code == 200
    job = client.post(
        "/api/v1/jobs",
        headers={**client_headers, "Idempotency-Key": "job-001"},
        json={
            "hostId": "host-01",
            "printerId": "printer-01",
            "metadata": {"copies": 1},
            "dataRef": "blob://private/job-001",
            "contentSha256": "a" * 64,
            "contentSize": 1024,
            "contentType": "application/pdf",
        },
    )
    assert job.status_code == 201
    return client, host_headers, client_headers, job.json()


def test_host_can_claim_and_advance_job_without_duplicate_claim(tmp_path):
    client, host_headers, client_headers, job = setup_job(tmp_path)
    job_id = job["jobId"]

    pending = client.get("/api/v1/jobs", headers=host_headers)
    assert pending.status_code == 200
    assert pending.json()[0]["jobId"] == job_id

    claimed = client.post(f"/api/v1/jobs/{job_id}/claim", headers=host_headers, json={"leaseSeconds": 60})
    assert claimed.status_code == 200
    claim_token = claimed.json()["claimToken"]
    assert claimed.json()["status"] == "CLAIMED"

    duplicate_claim = client.post(f"/api/v1/jobs/{job_id}/claim", headers=host_headers, json={})
    assert duplicate_claim.status_code == 409

    renewed = client.post(
        f"/api/v1/jobs/{job_id}/renew",
        headers=host_headers,
        json={"claimToken": claim_token, "leaseSeconds": 60},
    )
    assert renewed.status_code == 200
    assert renewed.json()["status"] == "CLAIMED"

    acknowledged = client.post(
        f"/api/v1/jobs/{job_id}/ack",
        headers=host_headers,
        json={"claimToken": claim_token},
    )
    assert acknowledged.status_code == 200
    assert acknowledged.json()["status"] == "DOWNLOADING"

    downloading = client.patch(
        f"/api/v1/jobs/{job_id}/status",
        headers=host_headers,
        json={"claimToken": claim_token, "status": "SPOOLING"},
    )
    assert downloading.status_code == 200

    status_update = client.patch(
        f"/api/v1/jobs/{job_id}/status",
        headers=host_headers,
        json={"claimToken": claim_token, "status": "SPOOLER_ACCEPTED", "spoolerJobId": "spool-42"},
    )
    assert status_update.status_code == 200
    assert status_update.json()["spoolerJobId"] == "spool-42"

    completed = client.patch(
        f"/api/v1/jobs/{job_id}/status",
        headers=host_headers,
        json={"claimToken": claim_token, "status": "COMPLETED", "spoolerJobId": "spool-42"},
    )
    assert completed.status_code == 200
    assert completed.json()["completedAt"] is not None

    visible_to_client = client.get(f"/api/v1/jobs/{job_id}", headers=client_headers)
    assert visible_to_client.status_code == 200
    assert visible_to_client.json()["status"] == "COMPLETED"


def test_client_can_cancel_queued_job_but_host_cannot_claim_it_afterwards(tmp_path):
    client, host_headers, client_headers, job = setup_job(tmp_path)
    canceled = client.post(
        f"/api/v1/jobs/{job['jobId']}/cancel",
        headers=client_headers,
        json={"reason": "User canceled"},
    )
    assert canceled.status_code == 200
    assert canceled.json()["status"] == "CANCELED"
    assert client.post(f"/api/v1/jobs/{job['jobId']}/claim", headers=host_headers, json={}).status_code == 409
