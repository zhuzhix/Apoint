USE astock_monitor;

DROP PROCEDURE IF EXISTS add_history_partition_retry_column;
DELIMITER //
CREATE PROCEDURE add_history_partition_retry_column(
    IN column_name_value VARCHAR(64),
    IN column_definition_value VARCHAR(512)
)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema=DATABASE()
          AND table_name='bar_ingest_partition'
          AND column_name=column_name_value
    ) THEN
        SET @ddl=CONCAT(
            'ALTER TABLE bar_ingest_partition ADD COLUMN `',
            column_name_value, '` ', column_definition_value
        );
        PREPARE statement_to_run FROM @ddl;
        EXECUTE statement_to_run;
        DEALLOCATE PREPARE statement_to_run;
    END IF;
END //
DELIMITER ;

CALL add_history_partition_retry_column('attempt_count',
    'SMALLINT UNSIGNED NOT NULL DEFAULT 0 AFTER process_id');
CALL add_history_partition_retry_column('max_attempts',
    'SMALLINT UNSIGNED NOT NULL DEFAULT 4 AFTER attempt_count');
CALL add_history_partition_retry_column('next_retry_at',
    'DATETIME(6) NULL AFTER max_attempts');
CALL add_history_partition_retry_column('failure_code',
    'VARCHAR(64) NULL AFTER next_retry_at');
CALL add_history_partition_retry_column('retryable',
    'BOOLEAN NOT NULL DEFAULT TRUE AFTER failure_code');
CALL add_history_partition_retry_column('owner_instance_id',
    'VARCHAR(128) NULL AFTER retryable');
CALL add_history_partition_retry_column('current_attempt_id',
    'BIGINT NULL AFTER owner_instance_id');
CALL add_history_partition_retry_column('completed_tasks',
    'INT NOT NULL DEFAULT 0 AFTER current_attempt_id');
CALL add_history_partition_retry_column('total_tasks',
    'INT NOT NULL DEFAULT 0 AFTER completed_tasks');
CALL add_history_partition_retry_column('last_symbol',
    'VARCHAR(32) NULL AFTER total_tasks');
CALL add_history_partition_retry_column('last_frequency',
    'VARCHAR(16) NULL AFTER last_symbol');
CALL add_history_partition_retry_column('last_checkpoint_date',
    'DATE NULL AFTER last_frequency');
CALL add_history_partition_retry_column('row_version',
    'BIGINT NOT NULL DEFAULT 0 AFTER last_checkpoint_date');
CALL add_history_partition_retry_column('manual_retry_count',
    'SMALLINT UNSIGNED NOT NULL DEFAULT 0 AFTER row_version');
DROP PROCEDURE IF EXISTS add_history_partition_retry_column;

DROP PROCEDURE IF EXISTS add_history_partition_retry_index;
DELIMITER //
CREATE PROCEDURE add_history_partition_retry_index(
    IN index_name_value VARCHAR(64),
    IN index_columns_value VARCHAR(512)
)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema=DATABASE()
          AND table_name='bar_ingest_partition'
          AND index_name=index_name_value
    ) THEN
        SET @ddl=CONCAT(
            'ALTER TABLE bar_ingest_partition ADD KEY `',
            index_name_value, '` ', index_columns_value
        );
        PREPARE statement_to_run FROM @ddl;
        EXECUTE statement_to_run;
        DEALLOCATE PREPARE statement_to_run;
    END IF;
END //
DELIMITER ;

CALL add_history_partition_retry_index('ix_bar_ingest_partition_retry',
    '(status, next_retry_at, partition_index)');
CALL add_history_partition_retry_index('ix_bar_ingest_partition_batch_status',
    '(batch_id, status)');
CALL add_history_partition_retry_index('ix_bar_ingest_partition_owner_health',
    '(owner_instance_id, status, heartbeat_at)');
DROP PROCEDURE IF EXISTS add_history_partition_retry_index;

CREATE TABLE IF NOT EXISTS bar_ingest_partition_attempt (
    id BIGINT NOT NULL AUTO_INCREMENT,
    partition_id VARCHAR(96) NOT NULL,
    batch_id BIGINT NOT NULL,
    attempt_number SMALLINT UNSIGNED NOT NULL,
    owner_instance_id VARCHAR(128) NOT NULL,
    process_id INT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'running',
    started_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    heartbeat_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    progress_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    finished_at DATETIME(6) NULL,
    rows_read BIGINT NOT NULL DEFAULT 0,
    rows_written BIGINT NOT NULL DEFAULT 0,
    rows_filtered BIGINT NOT NULL DEFAULT 0,
    completed_tasks INT NOT NULL DEFAULT 0,
    failure_code VARCHAR(64) NULL,
    error_message VARCHAR(2000) NULL,
    checkpoint_snapshot JSON NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_bar_partition_attempt_number (partition_id, attempt_number),
    KEY ix_bar_partition_attempt_batch (batch_id, started_at),
    KEY ix_bar_partition_attempt_failure (failure_code, finished_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS history_scheduler_lease (
    lease_name VARCHAR(96) NOT NULL,
    owner_instance_id VARCHAR(128) NOT NULL,
    fencing_token BIGINT NOT NULL DEFAULT 1,
    heartbeat_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    lease_expires_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (lease_name),
    KEY ix_history_scheduler_lease_expiry (lease_expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS history_control_command (
    id BIGINT NOT NULL AUTO_INCREMENT,
    request_id CHAR(36) NOT NULL,
    command_type VARCHAR(32) NOT NULL,
    batch_id BIGINT NULL,
    partition_id VARCHAR(96) NULL,
    reason VARCHAR(500) NOT NULL,
    requested_by VARCHAR(128) NOT NULL,
    requested_from VARCHAR(64) NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    claimed_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    error_message VARCHAR(2000) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_history_control_command_request (request_id),
    KEY ix_history_control_command_claim (status, created_at),
    KEY ix_history_control_command_partition (partition_id, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration (version, description)
VALUES ('016', 'history partition retries attempts scheduler lease monitoring commands')
ON DUPLICATE KEY UPDATE description=VALUES(description);
