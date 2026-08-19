USE astock_monitor;

-- V2 canonical bars keep the existing partitioned tables and add common
-- provenance/revision fields. The procedure makes this migration rerunnable.
DROP PROCEDURE IF EXISTS add_v2_bar_column;
DELIMITER //
CREATE PROCEDURE add_v2_bar_column(
    IN table_name_value VARCHAR(64),
    IN column_name_value VARCHAR(64),
    IN column_definition_value VARCHAR(512)
)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema=DATABASE()
          AND table_name=table_name_value
          AND column_name=column_name_value
    ) THEN
        SET @ddl=CONCAT(
            'ALTER TABLE `', table_name_value, '` ADD COLUMN `',
            column_name_value, '` ', column_definition_value
        );
        PREPARE statement_to_run FROM @ddl;
        EXECUTE statement_to_run;
        DEALLOCATE PREPARE statement_to_run;
    END IF;
END //
DELIMITER ;

CALL add_v2_bar_column('kline_bar_5m', 'source_priority',
    'SMALLINT NOT NULL DEFAULT 300 AFTER source');
CALL add_v2_bar_column('kline_bar_5m', 'source_updated_at',
    'DATETIME(6) NULL AFTER source_priority');
CALL add_v2_bar_column('kline_bar_5m', 'official_confirmed',
    'BOOLEAN NOT NULL DEFAULT TRUE AFTER source_updated_at');
CALL add_v2_bar_column('kline_bar_5m', 'revision',
    'INT NOT NULL DEFAULT 0 AFTER official_confirmed');
CALL add_v2_bar_column('kline_bar_5m', 'quality_status',
    'VARCHAR(24) NOT NULL DEFAULT ''passed'' AFTER revision');
CALL add_v2_bar_column('kline_bar_5m', 'recovery_run_id',
    'BIGINT NULL AFTER ingest_batch_id');

CALL add_v2_bar_column('kline_bar_agg', 'source_priority',
    'SMALLINT NOT NULL DEFAULT 100 AFTER source');
CALL add_v2_bar_column('kline_bar_agg', 'source_updated_at',
    'DATETIME(6) NULL AFTER source_priority');
CALL add_v2_bar_column('kline_bar_agg', 'official_confirmed',
    'BOOLEAN NOT NULL DEFAULT FALSE AFTER source_updated_at');
CALL add_v2_bar_column('kline_bar_agg', 'revision',
    'INT NOT NULL DEFAULT 0 AFTER official_confirmed');
CALL add_v2_bar_column('kline_bar_agg', 'quality_status',
    'VARCHAR(24) NOT NULL DEFAULT ''unchecked'' AFTER revision');
CALL add_v2_bar_column('kline_bar_agg', 'recovery_run_id',
    'BIGINT NULL AFTER ingest_batch_id');

CALL add_v2_bar_column('kline_bar_daily', 'source_priority',
    'SMALLINT NOT NULL DEFAULT 300 AFTER source');
CALL add_v2_bar_column('kline_bar_daily', 'source_updated_at',
    'DATETIME(6) NULL AFTER source_priority');
CALL add_v2_bar_column('kline_bar_daily', 'official_confirmed',
    'BOOLEAN NOT NULL DEFAULT TRUE AFTER source_updated_at');
CALL add_v2_bar_column('kline_bar_daily', 'revision',
    'INT NOT NULL DEFAULT 0 AFTER official_confirmed');
CALL add_v2_bar_column('kline_bar_daily', 'quality_status',
    'VARCHAR(24) NOT NULL DEFAULT ''passed'' AFTER revision');
CALL add_v2_bar_column('kline_bar_daily', 'recovery_run_id',
    'BIGINT NULL AFTER ingest_batch_id');

DROP PROCEDURE IF EXISTS add_v2_bar_column;

-- Existing 5m/daily rows were already sourced directly from GM, so the
-- INSTANT column defaults above classify them without rewriting millions of
-- partitioned rows. Only the much smaller legacy aggregate table needs source
-- classification because derived rows must not become official by default.
UPDATE kline_bar_agg
SET source_priority=IF(source LIKE 'dongcai-gm%', 300, 100),
    official_confirmed=(source LIKE 'dongcai-gm%'),
    quality_status=IF(source LIKE 'dongcai-gm%', 'passed', 'unchecked'),
    source_updated_at=COALESCE(source_updated_at, updated_at);

CREATE TABLE IF NOT EXISTS bar_sync_checkpoint (
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    last_seen_eob DATETIME(6) NULL,
    last_closed_eob DATETIME(6) NULL,
    last_persisted_eob DATETIME(6) NULL,
    last_reconciled_eob DATETIME(6) NULL,
    last_source_updated_at DATETIME(6) NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'healthy',
    consecutive_failures INT NOT NULL DEFAULT 0,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (symbol, frequency),
    KEY ix_bar_sync_status (status, updated_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS bar_event_outbox (
    id BIGINT NOT NULL AUTO_INCREMENT,
    event_id VARCHAR(160) NOT NULL,
    event_type VARCHAR(24) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    trading_date DATE NOT NULL,
    bob DATETIME(6) NOT NULL,
    eob DATETIME(6) NOT NULL,
    revision INT NOT NULL,
    row_hash CHAR(64) NOT NULL,
    payload JSON NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    attempt_count INT NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    published_at DATETIME(6) NULL,
    last_error VARCHAR(2000) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_bar_event_outbox_event (event_id),
    KEY ix_bar_event_outbox_publish (status, next_attempt_at, id),
    KEY ix_bar_event_outbox_symbol (symbol, frequency, eob)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS bar_reconcile_log (
    id BIGINT NOT NULL AUTO_INCREMENT,
    reconcile_key CHAR(64) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    trading_date DATE NOT NULL,
    eob DATETIME(6) NOT NULL,
    result_type VARCHAR(32) NOT NULL,
    old_row_hash CHAR(64) NULL,
    new_row_hash CHAR(64) NULL,
    old_payload JSON NULL,
    new_payload JSON NULL,
    reason VARCHAR(255) NULL,
    recovery_run_id BIGINT NULL,
    checked_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_bar_reconcile_key (reconcile_key),
    KEY ix_bar_reconcile_symbol (symbol, frequency, eob),
    KEY ix_bar_reconcile_result (result_type, checked_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS add_v2_recovery_column;
DELIMITER //
CREATE PROCEDURE add_v2_recovery_column(
    IN table_name_value VARCHAR(64),
    IN column_name_value VARCHAR(64),
    IN column_definition_value VARCHAR(512)
)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema=DATABASE()
          AND table_name=table_name_value
          AND column_name=column_name_value
    ) THEN
        SET @ddl=CONCAT(
            'ALTER TABLE `', table_name_value, '` ADD COLUMN `',
            column_name_value, '` ', column_definition_value
        );
        PREPARE statement_to_run FROM @ddl;
        EXECUTE statement_to_run;
        DEALLOCATE PREPARE statement_to_run;
    END IF;
END //
DELIMITER ;

CALL add_v2_recovery_column('market_data_gap', 'gap_type',
    'VARCHAR(32) NOT NULL DEFAULT ''missing_slot'' AFTER detect_method');
CALL add_v2_recovery_column('market_data_gap', 'next_retry_at',
    'DATETIME(6) NULL AFTER retry_count');
CALL add_v2_recovery_column('market_data_gap', 'source_delay_ms',
    'BIGINT NULL AFTER next_retry_at');
CALL add_v2_recovery_column('market_data_gap', 'source_available_from',
    'DATE NULL AFTER source_delay_ms');
CALL add_v2_recovery_column('market_data_gap', 'old_row_hash',
    'CHAR(64) NULL AFTER source_available_from');
CALL add_v2_recovery_column('market_data_gap', 'new_row_hash',
    'CHAR(64) NULL AFTER old_row_hash');

CALL add_v2_recovery_column('market_recovery_item', 'next_retry_at',
    'DATETIME(6) NULL AFTER retry_count');
CALL add_v2_recovery_column('market_recovery_item', 'source_available_from',
    'DATE NULL AFTER next_retry_at');
CALL add_v2_recovery_column('market_recovery_item', 'bars_unchanged',
    'BIGINT NOT NULL DEFAULT 0 AFTER rows_written');
CALL add_v2_recovery_column('market_recovery_item', 'events_published',
    'BIGINT NOT NULL DEFAULT 0 AFTER bars_unchanged');

CALL add_v2_recovery_column('market_recovery_run', 'bars_unchanged',
    'BIGINT NOT NULL DEFAULT 0 AFTER bars_revised');
CALL add_v2_recovery_column('market_recovery_run', 'verified_no_bar',
    'BIGINT NOT NULL DEFAULT 0 AFTER bars_unchanged');
CALL add_v2_recovery_column('market_recovery_run', 'source_expired',
    'BIGINT NOT NULL DEFAULT 0 AFTER verified_no_bar');
CALL add_v2_recovery_column('market_recovery_run', 'events_published',
    'BIGINT NOT NULL DEFAULT 0 AFTER source_expired');

DROP PROCEDURE IF EXISTS add_v2_recovery_column;

INSERT INTO data_retention_policy
    (dataset_name, retention_mode, cutoff_rule, archive_before_purge)
VALUES
    ('bar_event_outbox', 'rolling_purge',
     'Keep published canonical bar event audit for at least 12 months', TRUE),
    ('bar_reconcile_log', 'rolling_purge',
     'Keep detailed canonical bar revisions for at least 24 months', TRUE),
    ('bar_sync_checkpoint', 'keep_forever',
     'Keep the latest synchronization watermark for every symbol and frequency', FALSE)
ON DUPLICATE KEY UPDATE
    retention_mode=VALUES(retention_mode),
    cutoff_rule=VALUES(cutoff_rule),
    archive_before_purge=VALUES(archive_before_purge);

INSERT INTO schema_migration (version, description)
VALUES ('010', 'official K-line canonical storage, checkpoints, reliable outbox and V2 recovery metadata')
ON DUPLICATE KEY UPDATE description=VALUES(description);
