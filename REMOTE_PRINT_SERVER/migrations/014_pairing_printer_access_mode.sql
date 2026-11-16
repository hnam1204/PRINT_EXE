-- Explicitly separate all-shared, selected-only, and no-printer permissions.
ALTER TABLE pairings
    ADD COLUMN IF NOT EXISTS printer_access_mode VARCHAR(16) NOT NULL DEFAULT 'ALL_SHARED';

UPDATE pairings
SET printer_access_mode = CASE
    WHEN allowed_printers IS NULL OR jsonb_array_length(COALESCE(allowed_printers::jsonb, '[]'::jsonb)) = 0
        THEN 'ALL_SHARED'
    ELSE 'SELECTED_ONLY'
END
WHERE printer_access_mode = 'ALL_SHARED';

ALTER TABLE pairings
    DROP CONSTRAINT IF EXISTS pairings_printer_access_mode_check;

ALTER TABLE pairings
    ADD CONSTRAINT pairings_printer_access_mode_check
    CHECK (printer_access_mode IN ('ALL_SHARED', 'SELECTED_ONLY', 'NONE'));
