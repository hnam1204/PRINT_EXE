CREATE TABLE IF NOT EXISTS telegram_messages (
    id VARCHAR(36) PRIMARY KEY,
    chat_id VARCHAR(64) NOT NULL,
    message_id INTEGER NOT NULL,
    direction VARCHAR(8) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_telegram_messages_chat_message
    ON telegram_messages(chat_id, message_id);
CREATE INDEX IF NOT EXISTS ix_telegram_messages_chat_id
    ON telegram_messages(chat_id);
