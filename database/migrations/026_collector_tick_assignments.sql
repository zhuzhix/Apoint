USE astock_monitor;

CREATE TABLE IF NOT EXISTS collector_tick_assignment (
    gateway_id VARCHAR(96) NOT NULL,
    worker_id VARCHAR(96) NOT NULL,
    assignment_version VARCHAR(96) NOT NULL,
    symbols JSON NOT NULL,
    command_id CHAR(36) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    applied_at DATETIME(6) NULL,
    last_error VARCHAR(1024) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (gateway_id, worker_id),
    UNIQUE KEY uk_collector_tick_assignment_command (command_id),
    KEY ix_collector_tick_assignment_status (gateway_id, status, updated_at),
    CONSTRAINT fk_collector_tick_assignment_command
        FOREIGN KEY (command_id) REFERENCES collector_command(command_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration (version,description)
VALUES ('026','versioned cloud tick assignments for collector gateway')
ON DUPLICATE KEY UPDATE description=VALUES(description);
