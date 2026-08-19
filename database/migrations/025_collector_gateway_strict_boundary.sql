USE astock_monitor;

-- Cloud control plane. A command is durable before a local CollectorGateway
-- may execute it; Python workers never read this table.
CREATE TABLE IF NOT EXISTS collector_gateway (
    gateway_id VARCHAR(96) NOT NULL,
    display_name VARCHAR(160) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'offline',
    protocol_version INT NOT NULL,
    last_seen_at DATETIME(6) NULL,
    last_error VARCHAR(1024) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (gateway_id),
    KEY ix_collector_gateway_status (status, last_seen_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS collector_command (
    command_id CHAR(36) NOT NULL,
    gateway_id VARCHAR(96) NOT NULL,
    worker_id VARCHAR(96) NULL,
    command_type VARCHAR(48) NOT NULL,
    payload JSON NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    attempt_count INT NOT NULL DEFAULT 0,
    dispatched_at DATETIME(6) NULL,
    acknowledged_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    expires_at DATETIME(6) NOT NULL,
    last_error VARCHAR(1024) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (command_id),
    KEY ix_collector_command_dispatch (gateway_id, status, created_at),
    KEY ix_collector_command_expiry (status, expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS collector_result_batch (
    command_id CHAR(36) NOT NULL,
    batch_id CHAR(36) NOT NULL,
    gateway_id VARCHAR(96) NOT NULL,
    worker_id VARCHAR(96) NOT NULL,
    result_type VARCHAR(32) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'received',
    item_count INT NOT NULL,
    payload_hash CHAR(64) NOT NULL,
    received_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    applied_at DATETIME(6) NULL,
    last_error VARCHAR(1024) NULL,
    PRIMARY KEY (command_id, batch_id),
    KEY ix_collector_result_batch_apply (status, received_at),
    CONSTRAINT fk_collector_result_batch_command
        FOREIGN KEY (command_id) REFERENCES collector_command(command_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS official_bar_staging (
    command_id CHAR(36) NOT NULL,
    batch_id CHAR(36) NOT NULL,
    item_index INT NOT NULL,
    recovery_item_id BIGINT NOT NULL,
    payload JSON NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'received',
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    applied_at DATETIME(6) NULL,
    PRIMARY KEY (command_id, batch_id, item_index),
    KEY ix_official_bar_staging_apply (status, recovery_item_id, created_at),
    CONSTRAINT fk_official_bar_staging_batch
        FOREIGN KEY (command_id, batch_id)
        REFERENCES collector_result_batch(command_id, batch_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_replay_task (
    task_id CHAR(36) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    source_command_id CHAR(36) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    attempt_count INT NOT NULL DEFAULT 0,
    last_error VARCHAR(1024) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    PRIMARY KEY (task_id),
    UNIQUE KEY uk_strategy_replay_task_source
        (source_command_id, symbol, date_from, date_to),
    KEY ix_strategy_replay_task_claim (status, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration (version,description)
VALUES ('025','strict collector gateway command, batch inbox and strategy replay tasks')
ON DUPLICATE KEY UPDATE description=VALUES(description);
