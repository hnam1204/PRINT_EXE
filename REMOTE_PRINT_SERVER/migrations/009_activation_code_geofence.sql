-- Store the customer-specific geofence radius requested by the administrator bot.
-- Existing activation codes retain the current 1 km default.
ALTER TABLE activation_codes
    ADD COLUMN IF NOT EXISTS geofence_radius_meters INTEGER NOT NULL DEFAULT 1000;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'activation_codes_geofence_radius_check'
    ) THEN
        ALTER TABLE activation_codes
            ADD CONSTRAINT activation_codes_geofence_radius_check
            CHECK (geofence_radius_meters BETWEEN 1 AND 1000);
    END IF;
END $$;
