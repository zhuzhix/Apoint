USE astock_monitor;

CREATE TABLE IF NOT EXISTS market_data_watermark (
    symbol VARCHAR(32) NOT NULL,
    dataset VARCHAR(16) NOT NULL,
    collector_event_time DATETIME(6) NULL,
    stream_event_time DATETIME(6) NULL,
    durable_event_time DATETIME(6) NULL,
    bar_closed_time DATETIME(6) NULL,
    official_confirmed_time DATETIME(6) NULL,
    strategy_completed_time DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (symbol, dataset),
    KEY ix_market_watermark_updated (updated_at, dataset)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS market_recovery_run (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_key VARCHAR(160) NOT NULL,
    trigger_type VARCHAR(24) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'detected',
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    cutover_time DATETIME(6) NULL,
    overlap_seconds INT NOT NULL DEFAULT 120,
    dry_run BOOLEAN NOT NULL DEFAULT TRUE,
    requested_symbols INT NOT NULL DEFAULT 0,
    completed_symbols INT NOT NULL DEFAULT 0,
    failed_symbols INT NOT NULL DEFAULT 0,
    gaps_detected BIGINT NOT NULL DEFAULT 0,
    bars_downloaded BIGINT NOT NULL DEFAULT 0,
    bars_inserted BIGINT NOT NULL DEFAULT 0,
    bars_revised BIGINT NOT NULL DEFAULT 0,
    ticks_replayed BIGINT NOT NULL DEFAULT 0,
    quality_issue_count BIGINT NOT NULL DEFAULT 0,
    strategy_events_recalculated BIGINT NOT NULL DEFAULT 0,
    request_json JSON NULL,
    result_json JSON NULL,
    error_message VARCHAR(2000) NULL,
    started_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    finished_at DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_market_recovery_run_key (run_key),
    KEY ix_market_recovery_run_status (status, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS market_data_gap (
    id BIGINT NOT NULL AUTO_INCREMENT,
    gap_key CHAR(64) NOT NULL,
    scope_type VARCHAR(24) NOT NULL DEFAULT 'symbol',
    symbol VARCHAR(32) NOT NULL,
    dataset VARCHAR(16) NOT NULL,
    frequency VARCHAR(16) NULL,
    trading_date DATE NOT NULL,
    gap_start DATETIME(6) NOT NULL,
    gap_end DATETIME(6) NOT NULL,
    detect_method VARCHAR(32) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'detected',
    severity VARCHAR(16) NOT NULL DEFAULT 'warning',
    expected_count INT NOT NULL DEFAULT 0,
    local_count INT NOT NULL DEFAULT 0,
    recovered_count INT NOT NULL DEFAULT 0,
    missing_count INT NOT NULL DEFAULT 0,
    tick_recoverable BOOLEAN NULL,
    recovery_source VARCHAR(32) NULL,
    recovery_run_id BIGINT NULL,
    retry_count INT NOT NULL DEFAULT 0,
    last_error VARCHAR(2000) NULL,
    detected_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    validated_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_market_data_gap_key (gap_key),
    KEY ix_market_data_gap_status (status, severity, detected_at),
    KEY ix_market_data_gap_symbol (symbol, dataset, trading_date),
    KEY ix_market_data_gap_run (recovery_run_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS market_recovery_item (
    id BIGINT NOT NULL AUTO_INCREMENT,
    recovery_run_id BIGINT NOT NULL,
    gap_id BIGINT NULL,
    symbol VARCHAR(32) NOT NULL,
    dataset VARCHAR(16) NOT NULL,
    frequency VARCHAR(16) NULL,
    gap_start DATETIME(6) NOT NULL,
    gap_end DATETIME(6) NOT NULL,
    next_time DATETIME(6) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'planned',
    lease_owner VARCHAR(128) NULL,
    lease_expires_at DATETIME(6) NULL,
    retry_count INT NOT NULL DEFAULT 0,
    rows_read BIGINT NOT NULL DEFAULT 0,
    rows_written BIGINT NOT NULL DEFAULT 0,
    last_error VARCHAR(2000) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_market_recovery_item
        (recovery_run_id, symbol, dataset, gap_start),
    KEY ix_market_recovery_item_claim (status, lease_expires_at, recovery_run_id),
    KEY ix_market_recovery_item_gap (gap_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS kline_bar_1m (
    id BIGINT NOT NULL AUTO_INCREMENT,
    symbol VARCHAR(32) NOT NULL,
    trading_date DATE NOT NULL,
    bob DATETIME(6) NOT NULL,
    eob DATETIME(6) NOT NULL,
    open_price DECIMAL(20,6) NOT NULL,
    high_price DECIMAL(20,6) NOT NULL,
    low_price DECIMAL(20,6) NOT NULL,
    close_price DECIMAL(20,6) NOT NULL,
    pre_close DECIMAL(20,6) NULL,
    volume BIGINT UNSIGNED NOT NULL DEFAULT 0,
    amount DECIMAL(28,4) NOT NULL DEFAULT 0,
    source VARCHAR(32) NOT NULL,
    adjust_mode VARCHAR(16) NOT NULL DEFAULT 'none',
    ingest_batch_id BIGINT NULL,
    recovery_run_id BIGINT NULL,
    row_hash CHAR(64) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id, trading_date),
    UNIQUE KEY uk_kline_1m_symbol_eob (symbol, eob, trading_date),
    KEY ix_kline_1m_symbol_date (symbol, trading_date, eob),
    KEY ix_kline_1m_batch (ingest_batch_id, trading_date),
    KEY ix_kline_1m_recovery (recovery_run_id, trading_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
PARTITION BY RANGE COLUMNS(trading_date) (
    PARTITION p202601 VALUES LESS THAN ('2026-02-01'),
    PARTITION p202602 VALUES LESS THAN ('2026-03-01'),
    PARTITION p202603 VALUES LESS THAN ('2026-04-01'),
    PARTITION p202604 VALUES LESS THAN ('2026-05-01'),
    PARTITION p202605 VALUES LESS THAN ('2026-06-01'),
    PARTITION p202606 VALUES LESS THAN ('2026-07-01'),
    PARTITION p202607 VALUES LESS THAN ('2026-08-01'),
    PARTITION p202608 VALUES LESS THAN ('2026-09-01'),
    PARTITION p202609 VALUES LESS THAN ('2026-10-01'),
    PARTITION p202610 VALUES LESS THAN ('2026-11-01'),
    PARTITION p202611 VALUES LESS THAN ('2026-12-01'),
    PARTITION p202612 VALUES LESS THAN ('2027-01-01'),
    PARTITION pmax VALUES LESS THAN (MAXVALUE)
);

-- The migration may be rerun after an earlier version created kline_bar_1m.
-- Add the shared ingestion column and index defensively for those databases.
DROP PROCEDURE IF EXISTS ensure_kline_1m_ingest_batch;
DELIMITER //
CREATE PROCEDURE ensure_kline_1m_ingest_batch()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema=DATABASE() AND table_name='kline_bar_1m'
          AND column_name='ingest_batch_id'
    ) THEN
        ALTER TABLE kline_bar_1m ADD COLUMN ingest_batch_id BIGINT NULL AFTER adjust_mode;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema=DATABASE() AND table_name='kline_bar_1m'
          AND index_name='ix_kline_1m_batch'
    ) THEN
        CREATE INDEX ix_kline_1m_batch ON kline_bar_1m (ingest_batch_id, trading_date);
    END IF;
END //
DELIMITER ;
CALL ensure_kline_1m_ingest_batch();
DROP PROCEDURE IF EXISTS ensure_kline_1m_ingest_batch;

DROP PROCEDURE IF EXISTS add_quote_bar_recovery_column;
DELIMITER //
CREATE PROCEDURE add_quote_bar_recovery_column(
    IN column_name_value VARCHAR(64),
    IN column_definition_value VARCHAR(512)
)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema=DATABASE() AND table_name='quote_bar'
          AND column_name=column_name_value
    ) THEN
        SET @ddl=CONCAT('ALTER TABLE quote_bar ADD COLUMN ',
                        column_name_value, ' ', column_definition_value);
        PREPARE statement_to_run FROM @ddl;
        EXECUTE statement_to_run;
        DEALLOCATE PREPARE statement_to_run;
    END IF;
END //
DELIMITER ;

CALL add_quote_bar_recovery_column('source_priority',
    'SMALLINT NOT NULL DEFAULT 200 AFTER source');
CALL add_quote_bar_recovery_column('recovery_run_id',
    'BIGINT NULL AFTER source_priority');
CALL add_quote_bar_recovery_column('is_replay',
    'BOOLEAN NOT NULL DEFAULT FALSE AFTER recovery_run_id');
CALL add_quote_bar_recovery_column('recovered_at',
    'DATETIME(6) NULL AFTER is_replay');
CALL add_quote_bar_recovery_column('quality_status',
    'VARCHAR(24) NOT NULL DEFAULT ''unchecked'' AFTER recovered_at');
DROP PROCEDURE IF EXISTS add_quote_bar_recovery_column;

DROP PROCEDURE IF EXISTS add_realtime_bar_event_recovery_column;
DELIMITER //
CREATE PROCEDURE add_realtime_bar_event_recovery_column(
    IN column_name_value VARCHAR(64),
    IN column_definition_value VARCHAR(512)
)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema=DATABASE() AND table_name='realtime_bar_event'
          AND column_name=column_name_value
    ) THEN
        SET @ddl=CONCAT('ALTER TABLE realtime_bar_event ADD COLUMN ',
                        column_name_value, ' ', column_definition_value);
        PREPARE statement_to_run FROM @ddl;
        EXECUTE statement_to_run;
        DEALLOCATE PREPARE statement_to_run;
    END IF;
END //
DELIMITER ;

CALL add_realtime_bar_event_recovery_column('is_replay',
    'BOOLEAN NOT NULL DEFAULT FALSE AFTER cause_event_id');
CALL add_realtime_bar_event_recovery_column('recovery_run_id',
    'BIGINT NULL AFTER is_replay');
CALL add_realtime_bar_event_recovery_column('recovery_reason',
    'VARCHAR(255) NULL AFTER recovery_run_id');
DROP PROCEDURE IF EXISTS add_realtime_bar_event_recovery_column;

INSERT INTO data_retention_policy
    (dataset_name, retention_mode, cutoff_rule, archive_before_purge)
VALUES
    ('kline_bar_1m', 'annual_partition_archive',
     'In January archive and purge trading_date before July 1 of previous year', TRUE),
    ('market_data_gap', 'rolling_purge',
     'Keep completed recovery audit records for 24 months', TRUE),
    ('market_recovery_run', 'rolling_purge',
     'Keep recovery run audit records for 24 months', TRUE)
ON DUPLICATE KEY UPDATE
    retention_mode=VALUES(retention_mode), cutoff_rule=VALUES(cutoff_rule),
    archive_before_purge=VALUES(archive_before_purge);

INSERT INTO schema_migration (version, description)
VALUES ('007', 'market gap detection, recovery orchestration and 1m foundation')
ON DUPLICATE KEY UPDATE description=VALUES(description);
