CREATE TABLE IF NOT EXISTS activation_codes (
    id VARCHAR(36) PRIMARY KEY,
    code_hash VARCHAR(64) NOT NULL UNIQUE,
    code_hint VARCHAR(8) NOT NULL,
    label VARCHAR(160) NOT NULL DEFAULT 'customer',
    max_devices INTEGER NOT NULL DEFAULT 1,
    used_devices INTEGER NOT NULL DEFAULT 0,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL,
    created_by VARCHAR(128) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_activation_codes_expires_at ON activation_codes(expires_at);
