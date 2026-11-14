-- PostgreSQL schema for production. Local development may use SQLAlchemy create_all.
CREATE TABLE IF NOT EXISTS devices (
    id VARCHAR(128) PRIMARY KEY,
    name VARCHAR(160) NOT NULL,
    role VARCHAR(16) NOT NULL CHECK (role IN ('HOST', 'CLIENT')),
    app_version VARCHAR(32) NOT NULL,
    token_hash VARCHAR(128) NOT NULL UNIQUE,
    public_key TEXT,
    key_algorithm VARCHAR(40) NOT NULL DEFAULT 'RSA-OAEP-SHA256',
    geofence_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    geofence_latitude DOUBLE PRECISION,
    geofence_longitude DOUBLE PRECISION,
    geofence_radius_meters DOUBLE PRECISION NOT NULL DEFAULT 1000,
    is_revoked BOOLEAN NOT NULL DEFAULT FALSE,
    last_seen_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_devices_last_seen_at ON devices(last_seen_at);

CREATE TABLE IF NOT EXISTS pairings (
    id VARCHAR(36) PRIMARY KEY,
    host_id VARCHAR(128) NOT NULL REFERENCES devices(id),
    client_id VARCHAR(128) NOT NULL REFERENCES devices(id),
    code VARCHAR(32) NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'PENDING',
    attempts INTEGER NOT NULL DEFAULT 0,
    expires_at TIMESTAMPTZ NOT NULL,
    allowed_printers JSONB NOT NULL DEFAULT '[]'::jsonb,
    location_status VARCHAR(16) NOT NULL DEFAULT 'UNVERIFIED',
    client_latitude DOUBLE PRECISION,
    client_longitude DOUBLE PRECISION,
    client_accuracy_meters DOUBLE PRECISION,
    client_location_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL,
    decided_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_pairings_host_status ON pairings(host_id, status);
CREATE INDEX IF NOT EXISTS ix_pairings_client_status ON pairings(client_id, status);

CREATE TABLE IF NOT EXISTS pairing_codes (
    id VARCHAR(36) PRIMARY KEY,
    session_id VARCHAR(128) NOT NULL UNIQUE,
    device_id VARCHAR(128) NOT NULL REFERENCES devices(id),
    code_hash VARCHAR(64) NOT NULL UNIQUE,
    code_ciphertext TEXT NOT NULL,
    issued_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'ACTIVE',
    revoked_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_pairing_codes_device_status ON pairing_codes(device_id, status);

CREATE TABLE IF NOT EXISTS printers (
    id VARCHAR(128) PRIMARY KEY,
    host_id VARCHAR(128) NOT NULL REFERENCES devices(id),
    display_name VARCHAR(200) NOT NULL,
    reported_status VARCHAR(32) NOT NULL DEFAULT 'UNKNOWN',
    is_shared BOOLEAN NOT NULL DEFAULT FALSE,
    metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS print_jobs (
    id VARCHAR(36) PRIMARY KEY,
    idempotency_key VARCHAR(200) NOT NULL UNIQUE,
    client_id VARCHAR(128) NOT NULL REFERENCES devices(id),
    host_id VARCHAR(128) NOT NULL REFERENCES devices(id),
    printer_id VARCHAR(128) NOT NULL REFERENCES printers(id),
    status VARCHAR(24) NOT NULL DEFAULT 'QUEUED',
    request_hash VARCHAR(64) NOT NULL,
    metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    data_ref VARCHAR(512),
    content_sha256 VARCHAR(64),
    content_size INTEGER,
    content_type VARCHAR(120),
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    claimed_at TIMESTAMPTZ NULL,
    claim_token_hash VARCHAR(64),
    claim_expires_at TIMESTAMPTZ NULL,
    spooler_job_id VARCHAR(128),
    failure_code VARCHAR(64),
    failure_message VARCHAR(500),
    completed_at TIMESTAMPTZ NULL
);

CREATE TABLE IF NOT EXISTS print_job_tombstones (
    id VARCHAR(36) PRIMARY KEY,
    idempotency_key VARCHAR(200) NOT NULL UNIQUE,
    request_hash VARCHAR(64) NOT NULL,
    original_job_id VARCHAR(36) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_print_job_tombstones_original_job_id ON print_job_tombstones(original_job_id);

CREATE INDEX IF NOT EXISTS ix_print_jobs_host_status_created ON print_jobs(host_id, status, created_at);
CREATE INDEX IF NOT EXISTS ix_print_jobs_claim_expires_at ON print_jobs(claim_expires_at);

CREATE TABLE IF NOT EXISTS audit_events (
    id VARCHAR(36) PRIMARY KEY,
    actor_id VARCHAR(128),
    action VARCHAR(80) NOT NULL,
    target_type VARCHAR(40) NOT NULL,
    target_id VARCHAR(128),
    details JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS rate_limit_events (
    id VARCHAR(36) PRIMARY KEY,
    bucket VARCHAR(200) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_rate_limit_events_bucket_created ON rate_limit_events(bucket, created_at);

CREATE TABLE IF NOT EXISTS cleanup_runs (
    id VARCHAR(36) PRIMARY KEY,
    started_at TIMESTAMPTZ NOT NULL,
    finished_at TIMESTAMPTZ NOT NULL,
    status VARCHAR(32) NOT NULL,
    summary JSONB NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS ix_cleanup_runs_finished_at ON cleanup_runs(finished_at);
