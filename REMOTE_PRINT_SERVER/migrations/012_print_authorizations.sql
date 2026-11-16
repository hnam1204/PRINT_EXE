-- Short-lived backend authorization for each direct/P2P/Relay print job.
CREATE TABLE IF NOT EXISTS print_authorizations (
    id VARCHAR(36) PRIMARY KEY,
    token_hash VARCHAR(64) NOT NULL UNIQUE,
    job_id VARCHAR(200) NOT NULL,
    client_id VARCHAR(128) NOT NULL REFERENCES devices(id),
    host_id VARCHAR(128) NOT NULL REFERENCES devices(id),
    printer_id VARCHAR(128) NOT NULL REFERENCES printers(id),
    expires_at TIMESTAMPTZ NOT NULL,
    used_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_print_authorizations_job_id
    ON print_authorizations (job_id);
CREATE INDEX IF NOT EXISTS ix_print_authorizations_host_id
    ON print_authorizations (host_id, used_at, expires_at);
