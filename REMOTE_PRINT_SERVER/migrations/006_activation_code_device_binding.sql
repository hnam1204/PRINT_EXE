-- Bind a registered device to the customer activation code used for enrollment.
-- This prevents one code from being silently reused by unlimited devices.
ALTER TABLE devices ADD COLUMN IF NOT EXISTS activation_code_id VARCHAR(36) REFERENCES activation_codes(id);
CREATE INDEX IF NOT EXISTS ix_devices_activation_code_id ON devices(activation_code_id);
