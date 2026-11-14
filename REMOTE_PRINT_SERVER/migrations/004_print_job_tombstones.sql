-- Preserve idempotency keys after temporary Print Job metadata is removed.
CREATE TABLE IF NOT EXISTS print_job_tombstones (
    id VARCHAR(36) PRIMARY KEY,
    idempotency_key VARCHAR(200) NOT NULL UNIQUE,
    request_hash VARCHAR(64) NOT NULL,
    original_job_id VARCHAR(36) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_print_job_tombstones_original_job_id ON print_job_tombstones(original_job_id);
