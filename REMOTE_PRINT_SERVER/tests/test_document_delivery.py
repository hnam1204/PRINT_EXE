import hashlib
import os

from fastapi.testclient import TestClient

os.environ.setdefault("RPM_SERVER_ENV", "development")
os.environ.setdefault("ALLOWED_ORIGINS", "http://localhost:3000")
os.environ.setdefault("ENROLLMENT_SECRET", "test-enrollment-secret")

from app.main import create_app


class MemoryBlobStore:
    def __init__(self):
        self.items = {}

    def put(self, pathname, body, content_type):
        reference = f"memory://{pathname}"
        self.items[reference] = bytes(body)
        return reference

    def get(self, reference):
        return self.items[reference]

    def delete(self, reference):
        self.items.pop(reference, None)


def register(client, device_id, role, public_key=None):
    response = client.post(
        "/api/v1/devices/register",
        headers={"X-Enrollment-Secret": "test-enrollment-secret"},
        json={
            "deviceId": device_id,
            "name": device_id,
            "role": role,
            "appVersion": "1.0.0",
            "publicKey": public_key,
        },
    )
    assert response.status_code == 201
    return response.json()["deviceToken"]


def test_encrypted_document_requires_claim_and_round_trips_as_ciphertext(tmp_path):
    store = MemoryBlobStore()
    client = TestClient(create_app(database_url=f"sqlite:///{tmp_path / 'documents.db'}", blob_store=store))
    host_token = register(client, "host-01", "HOST", "host-public-key")
    client_token = register(client, "client-01", "CLIENT", "client-public-key")
    host_headers = {"Authorization": f"Bearer {host_token}"}
    client_headers = {"Authorization": f"Bearer {client_token}"}

    assert client.post(
        "/api/v1/printers",
        headers=host_headers,
        json={"printerId": "printer-01", "displayName": "Office", "isShared": True},
    ).status_code == 200
    pairing = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-01"}).json()
    assert client.post(
        f"/api/v1/pairings/{pairing['pairingId']}/decision",
        headers=host_headers,
        json={"approve": True},
    ).status_code == 200
    job = client.post(
        "/api/v1/jobs",
        headers={**client_headers, "Idempotency-Key": "document-job-1"},
        json={"hostId": "host-01", "printerId": "printer-01", "metadata": {}},
    ).json()
    ciphertext = b"RPM2 encrypted test document"
    upload = client.put(
        f"/api/v1/jobs/{job['jobId']}/document",
        headers=client_headers,
        files={"document": ("document.bin", ciphertext, "application/octet-stream")},
    )
    assert upload.status_code == 200
    assert upload.json()["dataRef"].startswith("memory://jobs/")
    assert upload.json()["contentSha256"] == hashlib.sha256(ciphertext).hexdigest()

    before_claim = client.get(
        f"/api/v1/jobs/{job['jobId']}/document",
        headers=host_headers,
    )
    assert before_claim.status_code == 401

    claim = client.post(f"/api/v1/jobs/{job['jobId']}/claim", headers=host_headers, json={}).json()
    downloaded = client.get(
        f"/api/v1/jobs/{job['jobId']}/document",
        headers={**host_headers, "X-Claim-Token": claim["claimToken"]},
    )
    assert downloaded.status_code == 200
    assert downloaded.content == ciphertext
    assert downloaded.headers["X-Content-SHA256"] == hashlib.sha256(ciphertext).hexdigest()


def test_document_upload_requires_existing_owned_job(tmp_path):
    store = MemoryBlobStore()
    client = TestClient(create_app(database_url=f"sqlite:///{tmp_path / 'documents.db'}", blob_store=store))
    client_token = register(client, "client-01", "CLIENT")
    # No job exists: the endpoint must not accept arbitrary object-storage writes.
    response = client.put(
        "/api/v1/jobs/not-a-job/document",
        headers={"Authorization": f"Bearer {client_token}"},
        files={"document": ("document.bin", b"ciphertext", "application/octet-stream")},
    )
    assert response.status_code == 404


def test_document_upload_enforces_four_mib_boundary(tmp_path):
    store = MemoryBlobStore()
    client = TestClient(create_app(database_url=f"sqlite:///{tmp_path / 'documents.db'}", blob_store=store))
    host_token = register(client, "host-01", "HOST", "host-public-key")
    client_token = register(client, "client-01", "CLIENT", "client-public-key")
    host_headers = {"Authorization": f"Bearer {host_token}"}
    client_headers = {"Authorization": f"Bearer {client_token}"}
    assert client.post(
        "/api/v1/printers",
        headers=host_headers,
        json={"printerId": "printer-01", "displayName": "Office", "isShared": True},
    ).status_code == 200
    pairing = client.post("/api/v1/pairings", headers=client_headers, json={"hostId": "host-01"}).json()
    assert client.post(
        f"/api/v1/pairings/{pairing['pairingId']}/decision",
        headers=host_headers,
        json={"approve": True},
    ).status_code == 200

    boundary = b"x" * (4 * 1024 * 1024)
    job = client.post(
        "/api/v1/jobs",
        headers={**client_headers, "Idempotency-Key": "document-job-limit"},
        json={"hostId": "host-01", "printerId": "printer-01", "metadata": {}, "contentSize": len(boundary)},
    ).json()
    accepted = client.put(
        f"/api/v1/jobs/{job['jobId']}/document",
        headers=client_headers,
        files={"document": ("document.bin", boundary, "application/octet-stream")},
    )
    assert accepted.status_code == 200

    too_large_job = client.post(
        "/api/v1/jobs",
        headers={**client_headers, "Idempotency-Key": "document-job-over-limit"},
        json={"hostId": "host-01", "printerId": "printer-01", "metadata": {}},
    ).json()
    rejected = client.put(
        f"/api/v1/jobs/{too_large_job['jobId']}/document",
        headers=client_headers,
        files={"document": ("document.bin", b"x" * (4 * 1024 * 1024 + 1), "application/octet-stream")},
    )
    assert rejected.status_code == 413
