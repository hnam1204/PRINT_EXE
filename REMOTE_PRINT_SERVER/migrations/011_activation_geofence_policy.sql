-- Make geofence policy explicit while preserving all existing customer codes.
ALTER TABLE activation_codes
    ADD COLUMN IF NOT EXISTS geofence_policy VARCHAR(16) NOT NULL DEFAULT 'LIMITED';

ALTER TABLE activation_codes
    ALTER COLUMN geofence_radius_meters DROP NOT NULL;

UPDATE activation_codes
SET geofence_policy = 'LIMITED', geofence_radius_meters = 1000
WHERE geofence_policy IS NULL
   OR geofence_policy NOT IN ('LIMITED', 'UNLIMITED');

ALTER TABLE activation_codes
    DROP CONSTRAINT IF EXISTS activation_codes_geofence_radius_check;

ALTER TABLE activation_codes
    ADD CONSTRAINT activation_codes_geofence_policy_check
    CHECK (
        (geofence_policy = 'UNLIMITED' AND geofence_radius_meters IS NULL)
        OR (
            geofence_policy = 'LIMITED'
            AND geofence_radius_meters BETWEEN 1 AND 5000
        )
    );
