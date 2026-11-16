-- Durable control-plane events. The Gateway reads these rows and delivers
-- only the recipient device's events over its authenticated WebSocket.
CREATE TABLE IF NOT EXISTS realtime_events (
    id BIGSERIAL PRIMARY KEY,
    event_type VARCHAR(80) NOT NULL,
    recipient_device_id VARCHAR(128) NOT NULL REFERENCES devices(id),
    host_id VARCHAR(128),
    client_id VARCHAR(128),
    pairing_id VARCHAR(36),
    job_id VARCHAR(36),
    correlation_id VARCHAR(128) NOT NULL,
    payload_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_realtime_events_recipient_id
    ON realtime_events (recipient_device_id, id);
CREATE INDEX IF NOT EXISTS ix_realtime_events_created_at
    ON realtime_events (created_at);
