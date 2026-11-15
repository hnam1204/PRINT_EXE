-- Allow administrator-issued activation codes to remain valid indefinitely
-- and activate an unlimited number of devices. Revocation remains available.
ALTER TABLE activation_codes ALTER COLUMN expires_at DROP NOT NULL;
ALTER TABLE activation_codes ALTER COLUMN max_devices DROP NOT NULL;
