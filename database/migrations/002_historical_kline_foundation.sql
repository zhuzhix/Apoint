USE astock_monitor;

CREATE TABLE IF NOT EXISTS schema_migration (
    version VARCHAR(64) NOT NULL,
    description VARCHAR(255) NOT NULL,
    applied_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (version)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS instrument_daily_status (
    symbol VARCHAR(32) NOT NULL,
    trading_date DATE NOT NULL,
    exchange VARCHAR(16) NOT NULL,
    name VARCHAR(128) NOT NULL,
    is_st BOOLEAN NOT NULL DEFAULT FALSE,
    is_suspended BOOLEAN NOT NULL DEFAULT FALSE,
    is_eligible BOOLEAN NOT NULL DEFAULT FALSE,
    adjust_factor DECIMAL(24,12) NULL,
    source VARCHAR(32) NOT NULL DEFAULT 'dongcai-gm',
    universe_version VARCHAR(64) NOT NULL,
    raw_attributes JSON NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (symbol, trading_date),
    KEY ix_instrument_daily_eligible (trading_date, is_eligible, is_suspended),
    KEY ix_instrument_daily_exchange (exchange, trading_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS bar_ingest_batch (
    id BIGINT NOT NULL AUTO_INCREMENT,
    batch_key VARCHAR(160) NOT NULL,
    job_type VARCHAR(32) NOT NULL,
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    frequencies VARCHAR(64) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'running',
    requested_symbols INT NOT NULL DEFAULT 0,
    completed_symbols INT NOT NULL DEFAULT 0,
    rows_read BIGINT NOT NULL DEFAULT 0,
    rows_written BIGINT NOT NULL DEFAULT 0,
    rows_filtered BIGINT NOT NULL DEFAULT 0,
    error_count INT NOT NULL DEFAULT 0,
    details JSON NULL,
    started_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    finished_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_bar_ingest_batch_key (batch_key),
    KEY ix_bar_ingest_batch_status (status, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS bar_ingest_checkpoint (
    scope_key VARCHAR(160) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    next_date DATE NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    rows_written BIGINT NOT NULL DEFAULT 0,
    retry_count INT NOT NULL DEFAULT 0,
    last_error VARCHAR(2000) NULL,
    lease_owner VARCHAR(128) NULL,
    lease_expires_at DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (scope_key, symbol, frequency),
    KEY ix_bar_checkpoint_status (scope_key, status, next_date),
    KEY ix_bar_checkpoint_lease (lease_expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS kline_bar_5m (
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
    row_hash CHAR(64) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id, trading_date),
    UNIQUE KEY uk_kline_5m_symbol_eob (symbol, eob, trading_date),
    KEY ix_kline_5m_symbol_date (symbol, trading_date, eob),
    KEY ix_kline_5m_batch (ingest_batch_id, trading_date)
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
    PARTITION p202701 VALUES LESS THAN ('2027-02-01'),
    PARTITION p202702 VALUES LESS THAN ('2027-03-01'),
    PARTITION p202703 VALUES LESS THAN ('2027-04-01'),
    PARTITION p202704 VALUES LESS THAN ('2027-05-01'),
    PARTITION p202705 VALUES LESS THAN ('2027-06-01'),
    PARTITION p202706 VALUES LESS THAN ('2027-07-01'),
    PARTITION p202707 VALUES LESS THAN ('2027-08-01'),
    PARTITION p202708 VALUES LESS THAN ('2027-09-01'),
    PARTITION p202709 VALUES LESS THAN ('2027-10-01'),
    PARTITION p202710 VALUES LESS THAN ('2027-11-01'),
    PARTITION p202711 VALUES LESS THAN ('2027-12-01'),
    PARTITION p202712 VALUES LESS THAN ('2028-01-01'),
    PARTITION pmax VALUES LESS THAN (MAXVALUE)
);

CREATE TABLE IF NOT EXISTS kline_bar_agg (
    id BIGINT NOT NULL AUTO_INCREMENT,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
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
    component_count SMALLINT UNSIGNED NOT NULL,
    expected_component_count SMALLINT UNSIGNED NOT NULL,
    source VARCHAR(32) NOT NULL DEFAULT 'derived-5m',
    algorithm_version VARCHAR(32) NOT NULL,
    ingest_batch_id BIGINT NULL,
    row_hash CHAR(64) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id, trading_date),
    UNIQUE KEY uk_kline_agg_symbol_eob (symbol, frequency, eob, trading_date),
    KEY ix_kline_agg_symbol_date (symbol, frequency, trading_date, eob),
    KEY ix_kline_agg_batch (ingest_batch_id, trading_date)
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
    PARTITION p202701 VALUES LESS THAN ('2027-02-01'),
    PARTITION p202702 VALUES LESS THAN ('2027-03-01'),
    PARTITION p202703 VALUES LESS THAN ('2027-04-01'),
    PARTITION p202704 VALUES LESS THAN ('2027-05-01'),
    PARTITION p202705 VALUES LESS THAN ('2027-06-01'),
    PARTITION p202706 VALUES LESS THAN ('2027-07-01'),
    PARTITION p202707 VALUES LESS THAN ('2027-08-01'),
    PARTITION p202708 VALUES LESS THAN ('2027-09-01'),
    PARTITION p202709 VALUES LESS THAN ('2027-10-01'),
    PARTITION p202710 VALUES LESS THAN ('2027-11-01'),
    PARTITION p202711 VALUES LESS THAN ('2027-12-01'),
    PARTITION p202712 VALUES LESS THAN ('2028-01-01'),
    PARTITION pmax VALUES LESS THAN (MAXVALUE)
);

CREATE TABLE IF NOT EXISTS kline_bar_daily (
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
    row_hash CHAR(64) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_kline_daily_symbol_date (symbol, trading_date),
    KEY ix_kline_daily_date (trading_date, symbol),
    KEY ix_kline_daily_batch (ingest_batch_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS bar_quality_run (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_key VARCHAR(160) NOT NULL,
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'running',
    bars_checked BIGINT NOT NULL DEFAULT 0,
    issue_count BIGINT NOT NULL DEFAULT 0,
    summary JSON NULL,
    started_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    finished_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_bar_quality_run_key (run_key),
    KEY ix_bar_quality_run_status (status, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS bar_quality_issue (
    id BIGINT NOT NULL AUTO_INCREMENT,
    issue_key CHAR(64) NOT NULL,
    run_id BIGINT NOT NULL,
    check_type VARCHAR(64) NOT NULL,
    symbol VARCHAR(32) NULL,
    frequency VARCHAR(16) NULL,
    trading_date DATE NULL,
    eob DATETIME(6) NULL,
    severity VARCHAR(16) NOT NULL,
    message VARCHAR(1000) NOT NULL,
    details JSON NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'open',
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    resolved_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_bar_quality_issue_key (issue_key),
    KEY ix_bar_quality_issue_run (run_id, severity),
    KEY ix_bar_quality_issue_symbol (symbol, trading_date, frequency)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_pivot_signal (
    id BIGINT NOT NULL AUTO_INCREMENT,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    trading_date DATE NOT NULL,
    bob DATETIME(6) NOT NULL,
    eob DATETIME(6) NOT NULL,
    confirmed_at DATETIME(6) NOT NULL,
    pivot_type VARCHAR(16) NOT NULL,
    pair_price DECIMAL(20,6) NOT NULL,
    price_ticks BIGINT NOT NULL,
    pair_code TINYINT UNSIGNED NOT NULL,
    atr14 DECIMAL(20,8) NULL,
    prominence_atr DECIMAL(20,8) NULL,
    volume_percentile DECIMAL(10,6) NULL,
    wick_ratio DECIMAL(10,6) NULL,
    confluence_count SMALLINT UNSIGNED NOT NULL DEFAULT 1,
    score DECIMAL(10,6) NOT NULL DEFAULT 0,
    algorithm_version VARCHAR(32) NOT NULL,
    algorithm_params JSON NOT NULL,
    source_row_hash CHAR(64) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_pivot (symbol, frequency, eob, pivot_type, algorithm_version),
    KEY ix_pair_pivot_date (trading_date, frequency, pivot_type),
    KEY ix_pair_pivot_symbol (symbol, frequency, confirmed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS daily_pipeline_run (
    id BIGINT NOT NULL AUTO_INCREMENT,
    trading_date DATE NOT NULL,
    pipeline_version VARCHAR(32) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'running',
    current_stage VARCHAR(64) NULL,
    metrics JSON NULL,
    error_message VARCHAR(2000) NULL,
    started_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    finished_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_daily_pipeline_date_version (trading_date, pipeline_version),
    KEY ix_daily_pipeline_status (status, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS data_retention_policy (
    dataset_name VARCHAR(64) NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    retention_mode VARCHAR(32) NOT NULL,
    cutoff_rule VARCHAR(255) NOT NULL,
    archive_before_purge BOOLEAN NOT NULL DEFAULT TRUE,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (dataset_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS archive_manifest (
    id BIGINT NOT NULL AUTO_INCREMENT,
    dataset_name VARCHAR(64) NOT NULL,
    partition_name VARCHAR(64) NOT NULL,
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    archive_path VARCHAR(1000) NOT NULL,
    row_count BIGINT NOT NULL,
    checksum_sha256 CHAR(64) NOT NULL,
    status VARCHAR(24) NOT NULL,
    archived_at DATETIME(6) NULL,
    purged_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_archive_manifest_partition (dataset_name, partition_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS maintenance_job_run (
    id BIGINT NOT NULL AUTO_INCREMENT,
    job_name VARCHAR(128) NOT NULL,
    run_key VARCHAR(160) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'running',
    dry_run BOOLEAN NOT NULL DEFAULT TRUE,
    details JSON NULL,
    error_message VARCHAR(2000) NULL,
    started_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    finished_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_maintenance_run_key (job_name, run_key),
    KEY ix_maintenance_status (status, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO data_retention_policy
    (dataset_name, retention_mode, cutoff_rule, archive_before_purge)
VALUES
    ('kline_bar_5m', 'annual_partition_archive', 'In January archive and purge trading_date before July 1 of previous year', TRUE),
    ('kline_bar_agg', 'annual_partition_archive', 'In January archive and purge trading_date before July 1 of previous year', TRUE),
    ('kline_bar_daily', 'keep_forever', 'No automatic purge', TRUE),
    ('pair_pivot_signal', 'keep_forever', 'No automatic purge', TRUE),
    ('instrument_daily_status', 'keep_forever', 'No automatic purge', TRUE)
ON DUPLICATE KEY UPDATE
    retention_mode = VALUES(retention_mode),
    cutoff_rule = VALUES(cutoff_rule),
    archive_before_purge = VALUES(archive_before_purge);

INSERT INTO schema_migration (version, description)
VALUES ('002', 'historical K-line foundation, partitions, quality and pair pivots')
ON DUPLICATE KEY UPDATE description=VALUES(description);
