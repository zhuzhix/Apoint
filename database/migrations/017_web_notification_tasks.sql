USE astock_monitor;

CREATE TABLE IF NOT EXISTS notification_task (
    id BIGINT NOT NULL AUTO_INCREMENT,
    task_key VARCHAR(191) NOT NULL,
    task_type VARCHAR(32) NOT NULL,
    source_id VARCHAR(128) NOT NULL,
    symbol VARCHAR(32) NULL,
    symbol_name VARCHAR(128) NULL,
    severity VARCHAR(24) NOT NULL DEFAULT 'normal',
    business_status VARCHAR(32) NOT NULL,
    revision INT NOT NULL DEFAULT 0,
    latest_event_id VARCHAR(96) NOT NULL,
    title VARCHAR(256) NOT NULL,
    summary VARCHAR(1000) NOT NULL,
    payload_json JSON NOT NULL,
    is_read BOOLEAN NOT NULL DEFAULT FALSE,
    is_starred BOOLEAN NOT NULL DEFAULT FALSE,
    user_status VARCHAR(24) NOT NULL DEFAULT 'active',
    first_seen_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NOT NULL,
    read_at DATETIME(6) NULL,
    handled_at DATETIME(6) NULL,
    archived_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_notification_task_key (task_key),
    KEY ix_notification_task_list (user_status, task_type, last_seen_at),
    KEY ix_notification_task_symbol (symbol, last_seen_at),
    KEY ix_notification_task_unread (is_read, is_starred, last_seen_at),
    KEY ix_notification_task_event (latest_event_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS notification_task_change (
    id BIGINT NOT NULL AUTO_INCREMENT,
    task_id BIGINT NOT NULL,
    event_id VARCHAR(96) NOT NULL,
    revision INT NOT NULL,
    change_type VARCHAR(24) NOT NULL,
    occurred_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_notification_change_event (event_id),
    KEY ix_notification_change_task (task_id, id),
    CONSTRAINT fk_notification_change_task
        FOREIGN KEY (task_id) REFERENCES notification_task(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration (version, description)
VALUES ('017', 'web notification tasks durable changes and user state')
ON DUPLICATE KEY UPDATE description=VALUES(description);

