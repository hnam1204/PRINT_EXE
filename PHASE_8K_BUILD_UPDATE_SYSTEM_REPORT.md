# Phase 8K - Build, Update & Deployment Verification

Ngày kiểm tra: 2026-11-16
Repository: `https://github.com/hnam1204/PRINT_EXE`
Commit source: `138a9b9`
Release: `v1.0.27 Beta`

## Kết luận

Bản Beta `v1.0.27` đã được build self-contained Windows x64, push lên GitHub `main`, gắn tag và phát hành trên GitHub Releases. Backend đã deploy Vercel Production và health endpoint trả về database `ready`.

Đây là bản Beta để kiểm thử có kiểm soát. Chưa kết luận Stable, Production Ready hoặc Physical Print Ready vì chưa có bằng chứng Windows hai máy, updater rollback thật, Cloud realtime staging và in giấy thật qua hai mạng độc lập.

## Artifact và checksum

| Artifact | Kích thước | SHA-256 | Kết quả |
|---|---:|---|---|
| `publish/RemotePrintManager.exe` | 120,168,185 bytes | `e98643a2c78beef52970bcf984cad117fbe98ff9e104c01f61b403c3452b000d` | PASS |
| `publish/RPM.Updater.exe` | 36,518,763 bytes | `6fe125788e8bcaac282f27136795e01f53084aab0d2082ed652d91f5fc20b249` | PASS |
| `publish/relay/RPM.Relay.exe` | 36,648,894 bytes | `9363e052f4ef02aed03cacc697c112279844db00dacae7332cfd6260543ba9c2` | PASS |
| `publish/RemotePrintManager_v1.0.27_Portable.zip` | 138,471,485 bytes | `c5e47edfc87b97793a170ba0b179a8a88cc466f46b5575d975b2b6a44914037c` | PASS |

ZIP được kiểm tra chỉ chứa `RemotePrintManager.exe`, `RPM.Updater.exe` và `version.json`. Manifest SHA-256 khớp EXE và chữ ký RSA-PSS hợp lệ theo public key trong source.

## Kiểm thử

- .NET tests: **PASS**, 62 passed, 0 failed, 0 skipped.
- Python tests: **PASS WITH EXPECTED SKIP**, 67 passed, 1 skipped.
- Python skip: `tests/test_database.py:28`, cần biến môi trường `RPM_TEST_POSTGRES_URL`; PostgreSQL integration chưa được xác minh.
- Python `compileall`: **PASS**.
- WPF/Updater/Realtime Gateway/Relay self-contained publish: **PASS**.
- Secret scan staged diff: **PASS**; private signing key và `publish/` không được commit.

## GitHub

- `main` đã push commit `138a9b9`.
- Tag `v1.0.27` đã push.
- Release `Remote Print Manager v1.0.27 Beta` đã publish ở chế độ pre-release.
- Sáu asset đã upload và digest GitHub khớp checksum local: EXE, ZIP, Updater, Relay, `version.json`, `version_beta.json`.

## Vercel và Backend

- Deployment Production: **READY**.
- Alias: `https://print-exe.vercel.app`.
- `GET /api/v1/health`: **HTTP 200**.
- Health response báo `status=ok`, `environment=production`, `dependencies.database=ready`.
- Không chạy migration Neon Production trong tác vụ này.

Health 200 chỉ chứng minh API và database dependency đang phản hồi; chưa chứng minh Cloud pairing, realtime event delivery, printer permission và migration 014 trên môi trường thật.

## Chưa xác minh trước Stable

- Windows 10/11 sạch và chạy EXE self-contained trên thiết bị thật.
- WPF tray, restart, DPAPI/config persistence và updater replacement rollback trên máy thật.
- Host/Client qua hai mạng Internet độc lập.
- Cloud realtime pairing/printer permission authenticated E2E.
- Windows Spooler, Ctrl+P và in giấy thật.
- PostgreSQL staging với migration 014 và concurrency.

## Verdict

```text
SOURCE_PUSH = PASS
WINDOWS_X64_BUILD = PASS
GITHUB_RELEASE_BETA = PASS
MANIFEST_HASH_SIGNATURE = PASS
VERCEL_DEPLOYMENT = PASS (health verified)
POSTGRES_INTEGRATION = NOT VERIFIED (missing RPM_TEST_POSTGRES_URL)
WINDOWS_RUNTIME_E2E = NOT TESTED
PHYSICAL_PRINT = NOT TESTED
STABLE_RELEASE = NOT READY
```
