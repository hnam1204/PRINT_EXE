-- Additive migration for databases that already applied 001_initial.sql.
-- Safe to run once; each column/index is guarded for repeatable deployment.
ALTER TABLE print_jobs ADD COLUMN IF NOT EXISTS data_ref VARCHAR(512);
ALTER TABLE print_jobs ADD COLUMN IF NOT EXISTS content_sha256 VARCHAR(64);
ALTER TABLE print_jobs ADD COLUMN IF NOT EXISTS content_size INTEGER;
ALTER TABLE print_jobs ADD COLUMN IF NOT EXISTS content_type VARCHAR(120);
ALTER TABLE print_jobs ADD COLUMN IF NOT EXISTS claim_token_hash VARCHAR(64);
ALTER TABLE print_jobs ADD COLUMN IF NOT EXISTS claim_expires_at TIMESTAMPTZ NULL;
ALTER TABLE print_jobs ADD COLUMN IF NOT EXISTS spooler_job_id VARCHAR(128);
ALTER TABLE print_jobs ADD COLUMN IF NOT EXISTS failure_code VARCHAR(64);
ALTER TABLE print_jobs ADD COLUMN IF NOT EXISTS failure_message VARCHAR(500);
ALTER TABLE print_jobs ADD COLUMN IF NOT EXISTS completed_at TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS ix_print_jobs_host_status_created ON print_jobs(host_id, status, created_at);
CREATE INDEX IF NOT EXISTS ix_print_jobs_claim_expires_at ON print_jobs(claim_expires_at);

ALTER TABLE devices ADD COLUMN IF NOT EXISTS public_key TEXT;
ALTER TABLE devices ADD COLUMN IF NOT EXISTS key_algorithm VARCHAR(40) NOT NULL DEFAULT 'RSA-OAEP-SHA256';
