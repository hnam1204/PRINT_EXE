# Remote Print Server

Independent coordination API for Remote Print Manager. This service stores device identity, pairing state, printer metadata and print-job metadata. It does **not** access Windows Spooler and does not receive or transmit document bytes.

## Local setup

Vercel is pinned to Python 3.12. Use Python 3.12 locally for an identical runtime; Python 3.11 remains usable for the current test suite when 3.12 is not installed.

```powershell
cd "D:\ĐỒ ÁN WEBSITE\GITHUB\PRINTEXE\REMOTE_PRINT_SERVER"
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements-dev.txt
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

For a new PostgreSQL database, run the migration once from a machine with `psql` access:

```powershell
$env:DATABASE_URL = "postgresql://USER:PASSWORD@HOST:5432/DATABASE?sslmode=require"
psql $env:DATABASE_URL -f .\migrations\001_initial.sql
```

The application auto-creates schema only for local SQLite tests. Production PostgreSQL relies on the migration and does not run `create_all()` during a Vercel cold start.

Do not use `localhost` as a remote Host address. The server is only a coordination layer; a future Windows client integration must still prove the Host endpoint and authorization before declaring a connection successful.

## Current API surface

- `GET /api/v1/health`: service health without secrets.
- `POST /api/v1/devices/register`: enroll or rotate a device token.
- `GET /api/v1/devices/me`: return the authenticated device profile.
- `POST /api/v1/devices/heartbeat`: update the authenticated device's last-seen timestamp.
- `POST /api/v1/pairing-codes/issue`: issue one new 6-character code for an authenticated application session.
- `GET /api/v1/pairing-codes/current`: read the authenticated device's latest code status.
- `POST /api/v1/pairing-codes/resolve`: let an authenticated Client resolve a Host code into a pending pairing request.
- `POST /api/v1/pairing-codes/revoke`: revoke a code owned by the authenticated device.
- `GET /api/v1/internal/cleanup`: protected Vercel Cron endpoint; requires `Authorization: Bearer $CRON_SECRET`.

Pairing codes use only `A-Z` and `0-9`, are generated with a cryptographically secure source, expire after `PAIRING_TTL_MINUTES`, and are never reused. The database stores a unique hash and encrypted value; the plaintext is returned only to the authenticated owner when issuing or reading its own current code.

## Vercel production variables

Required in the `Production` environment:

- `RPM_SERVER_ENV=production`
- `DATABASE_URL=<PostgreSQL connection URL>`
- `ENROLLMENT_SECRET=<long random secret>`
- `ADMIN_API_KEY=<separate long random secret>`
- `CRON_SECRET=<separate long random secret>`
- `ALLOWED_ORIGINS=https://<allowed-frontend-origin>`

Optional values use the source defaults: `PAIRING_TTL_MINUTES=10`, `MAX_PAIRING_ATTEMPTS=5`, `HEARTBEAT_TIMEOUT_SECONDS=120`, `RATE_LIMIT_WINDOW_SECONDS=900`, and `RATE_LIMIT_MAX_ATTEMPTS=10`.

Pairing, printer permissions, print-job coordination, release metadata, and production deployment are intentionally separate slices and are not claimed complete yet.

## Retention policy

Vercel Cron calls cleanup daily at `02:00 UTC`. Cleanup only removes terminal rejected/expired pairings, terminal print jobs whose `updated_at` is older than 72 hours, old audit events, and old rate-limit events. Devices, approved pairings, printers, active/expired pairing-code history, unfinished jobs, and idempotency records are retained. Cleanup runs are retained for 30 days.
