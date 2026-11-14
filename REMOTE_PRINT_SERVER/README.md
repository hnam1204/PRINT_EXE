# Remote Print Server

Independent coordination API for Remote Print Manager. This service stores device identity, pairing state, printer metadata and print-job metadata. It does **not** access Windows Spooler and does not receive or transmit document bytes.

## Local setup

Use Python 3.11 or newer with a compatible prebuilt dependency set. Python 3.14 may require a Rust toolchain when wheels are unavailable.

```powershell
cd "D:\ĐỒ ÁN WEBSITE\GITHUB\PRINTEXE\REMOTE_PRINT_SERVER"
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
$env:ENROLLMENT_SECRET = "replace-with-a-long-random-secret"
.\.venv\Scripts\python.exe -m uvicorn api.index:app --reload
```

Health check: `GET http://127.0.0.1:8000/api/v1/health`

API docs are available at `/api/v1/docs` in development only.

## Device registration

Registration is an enrollment operation and requires `X-Enrollment-Secret`. The response contains a device token once; the server stores only its SHA-256 hash.

```powershell
$body = @{ deviceId = "client-01"; name = "Office Client"; role = "CLIENT"; appVersion = "1.0.0" } | ConvertTo-Json
$registered = Invoke-RestMethod `
  -Method Post `
  -Uri http://127.0.0.1:8000/api/v1/devices/register `
  -Headers @{ "X-Enrollment-Secret" = $env:ENROLLMENT_SECRET } `
  -ContentType "application/json" `
  -Body $body

$registered.deviceToken
```

Use the token as `Authorization: Bearer <deviceToken>` for `/devices/me` and `/devices/heartbeat`. Keep it in the Windows client's protected local credential store; never commit or log it.

## Production rules

Set `RPM_SERVER_ENV=production`, an explicit PostgreSQL `DATABASE_URL`, a long random `ENROLLMENT_SECRET`, a separate `ADMIN_API_KEY`, and HTTPS-only `ALLOWED_ORIGINS`. Apply `migrations/001_initial.sql` through the database migration process before serving traffic.

Do not use `localhost` as a remote Host address. The server is only a coordination layer; a future Windows client integration must still prove the Host endpoint and authorization before declaring a connection successful.

## Current API surface

- `GET /api/v1/health`: service health without secrets.
- `POST /api/v1/devices/register`: enroll or rotate a device token.
- `GET /api/v1/devices/me`: return the authenticated device profile.
- `POST /api/v1/devices/heartbeat`: update the authenticated device's last-seen timestamp.

Pairing, printer permissions, print-job coordination, release metadata, and production deployment are intentionally separate slices and are not claimed complete yet.
