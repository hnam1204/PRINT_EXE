-- Indexes used by the retention cleanup introduced after migration 014.
CREATE INDEX IF NOT EXISTS ix_pairing_codes_expires_at
    ON pairing_codes(expires_at);

CREATE INDEX IF NOT EXISTS ix_print_job_tombstones_expires_at
    ON print_job_tombstones(expires_at);

CREATE INDEX IF NOT EXISTS ix_telegram_messages_created_at
    ON telegram_messages(created_at);
