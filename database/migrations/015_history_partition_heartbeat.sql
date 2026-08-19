USE astock_monitor;

-- One row represents one isolated history-download OS process.  Heartbeats and
-- progress watermarks are partition-scoped so a watchdog never has to infer
-- health from another partition's checkpoint activity.
CREATE TABLE IF NOT EXISTS bar_ingest_partition (
    partition_id VARCHAR(96) NOT NULL,
    batch_id BIGINT NOT NULL,
    scope_key VARCHAR(160) NOT NULL,
    partition_index INT NOT NULL,
    symbol_count INT NOT NULL,
    symbols_json JSON NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    process_id INT NULL,
    heartbeat_at DATETIME(6) NULL,
    progress_at DATETIME(6) NULL,
    rows_read BIGINT NOT NULL DEFAULT 0,
    rows_written BIGINT NOT NULL DEFAULT 0,
    rows_filtered BIGINT NOT NULL DEFAULT 0,
    error_count INT NOT NULL DEFAULT 0,
    last_error VARCHAR(2000) NULL,
    started_at DATETIME(6) NULL,
    finished_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (partition_id),
    UNIQUE KEY uk_bar_ingest_partition_batch_index (batch_id, partition_index),
    KEY ix_bar_ingest_partition_health (status, heartbeat_at, progress_at),
    KEY ix_bar_ingest_partition_scope (scope_key, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration (version, description)
VALUES ('015', 'partition-scoped history worker heartbeat and watchdog isolation')
ON DUPLICATE KEY UPDATE description=VALUES(description);
