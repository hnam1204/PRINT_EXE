-- Release expired leases before enforcing uniqueness. PostgreSQL partial indexes
-- cannot use CURRENT_TIMESTAMP in their predicate, so the API closes expired
-- rows before inserting a replacement.
UPDATE pairings
SET status = 'REVOKED',
    decided_at = COALESCE(decided_at, CURRENT_TIMESTAMP)
WHERE status = 'PENDING'
  AND expires_at <= CURRENT_TIMESTAMP;

-- Prevent concurrent retries from creating multiple live requests for one Host/Client pair.
-- Existing duplicates are retained as revoked history; the newest live row remains authoritative.
WITH ranked AS (
    SELECT id,
           ROW_NUMBER() OVER (
               PARTITION BY host_id, client_id
               ORDER BY created_at DESC, id DESC
           ) AS row_number
    FROM pairings
    WHERE status IN ('PENDING', 'APPROVED')
)
UPDATE pairings
SET status = 'REVOKED',
    decided_at = COALESCE(decided_at, CURRENT_TIMESTAMP)
WHERE id IN (SELECT id FROM ranked WHERE row_number > 1);

CREATE UNIQUE INDEX IF NOT EXISTS uq_pairings_live_host_client
    ON pairings(host_id, client_id)
    WHERE status IN ('PENDING', 'APPROVED');
