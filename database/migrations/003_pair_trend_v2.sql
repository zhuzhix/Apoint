USE astock_monitor;

CREATE TABLE IF NOT EXISTS pair_trend_backtest_run (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_key VARCHAR(200) NOT NULL,
    algorithm_version VARCHAR(32) NOT NULL,
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    frequencies VARCHAR(64) NOT NULL,
    parameters_json JSON NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'running',
    requested_symbols INT NOT NULL DEFAULT 0,
    completed_symbols INT NOT NULL DEFAULT 0,
    failed_symbols INT NOT NULL DEFAULT 0,
    bars_processed BIGINT NOT NULL DEFAULT 0,
    hits_detected BIGINT NOT NULL DEFAULT 0,
    events_written BIGINT NOT NULL DEFAULT 0,
    error_message VARCHAR(2000) NULL,
    started_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    finished_at DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_run_key (run_key),
    KEY ix_pair_trend_run_status (status, started_at),
    KEY ix_pair_trend_run_dates (date_from, date_to)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_trend_backtest_symbol (
    run_id BIGINT NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    bars_processed BIGINT NOT NULL DEFAULT 0,
    hits_detected BIGINT NOT NULL DEFAULT 0,
    events_written BIGINT NOT NULL DEFAULT 0,
    error_message VARCHAR(2000) NULL,
    started_at DATETIME(6) NULL,
    finished_at DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (run_id, symbol),
    KEY ix_pair_trend_symbol_status (run_id, status, symbol),
    CONSTRAINT fk_pair_trend_symbol_run
        FOREIGN KEY (run_id) REFERENCES pair_trend_backtest_run(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_trend_event (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_id BIGINT NOT NULL,
    event_key CHAR(64) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    symbol_name VARCHAR(128) NULL,
    pivot_type VARCHAR(16) NOT NULL,
    status VARCHAR(24) NOT NULL,
    first_seen_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NOT NULL,
    confirmed_at DATETIME(6) NULL,
    latest_pair_price DECIMAL(20,6) NOT NULL,
    latest_pair_code TINYINT UNSIGNED NOT NULL,
    latest_pair_kind VARCHAR(24) NOT NULL,
    timeframe_mask TINYINT UNSIGNED NOT NULL,
    frequencies VARCHAR(64) NOT NULL,
    strongest_frequency VARCHAR(16) NOT NULL,
    confluence_count SMALLINT UNSIGNED NOT NULL,
    total_hit_count INT UNSIGNED NOT NULL,
    confirmed_hit_count INT UNSIGNED NOT NULL DEFAULT 0,
    invalidated_hit_count INT UNSIGNED NOT NULL DEFAULT 0,
    pending_hit_count INT UNSIGNED NOT NULL DEFAULT 0,
    round_00_hit_count INT UNSIGNED NOT NULL DEFAULT 0,
    double_digit_hit_count INT UNSIGNED NOT NULL DEFAULT 0,
    score DECIMAL(10,6) NOT NULL DEFAULT 0,
    max_trend_strength DECIMAL(20,8) NOT NULL DEFAULT 0,
    algorithm_version VARCHAR(32) NOT NULL,
    summary_json JSON NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_event (run_id, event_key),
    KEY ix_pair_trend_event_filter
        (run_id, pivot_type, status, last_seen_at, id),
    KEY ix_pair_trend_event_symbol
        (symbol, last_seen_at, pivot_type),
    KEY ix_pair_trend_event_score (run_id, score, id),
    CONSTRAINT fk_pair_trend_event_run
        FOREIGN KEY (run_id) REFERENCES pair_trend_backtest_run(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_trend_hit (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_id BIGINT NOT NULL,
    event_id BIGINT NOT NULL,
    hit_key CHAR(64) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    trading_date DATE NOT NULL,
    bob DATETIME(6) NOT NULL,
    eob DATETIME(6) NOT NULL,
    observed_at DATETIME(6) NOT NULL,
    confirmed_at DATETIME(6) NULL,
    pivot_type VARCHAR(16) NOT NULL,
    status VARCHAR(24) NOT NULL,
    pair_price DECIMAL(20,6) NOT NULL,
    pair_code TINYINT UNSIGNED NOT NULL,
    pair_kind VARCHAR(24) NOT NULL,
    hit_field VARCHAR(16) NOT NULL,
    trend_direction VARCHAR(16) NOT NULL,
    trend_strength DECIMAL(20,8) NOT NULL,
    ema20 DECIMAL(20,8) NOT NULL,
    ema60 DECIMAL(20,8) NOT NULL,
    atr14 DECIMAL(20,8) NOT NULL,
    previous_close DECIMAL(20,6) NULL,
    open_price DECIMAL(20,6) NOT NULL,
    high_price DECIMAL(20,6) NOT NULL,
    low_price DECIMAL(20,6) NOT NULL,
    close_price DECIMAL(20,6) NOT NULL,
    volume BIGINT UNSIGNED NOT NULL,
    amount DECIMAL(28,4) NOT NULL,
    is_rolling_extreme BOOLEAN NOT NULL,
    volume_percentile DECIMAL(10,6) NOT NULL,
    wick_ratio DECIMAL(10,6) NOT NULL,
    reversal_atr DECIMAL(20,8) NOT NULL DEFAULT 0,
    score DECIMAL(10,6) NOT NULL DEFAULT 0,
    confirmation_reason VARCHAR(64) NULL,
    source_row_hash CHAR(64) NOT NULL,
    algorithm_version VARCHAR(32) NOT NULL,
    details_json JSON NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_hit (run_id, hit_key),
    KEY ix_pair_trend_hit_event (event_id, observed_at, id),
    KEY ix_pair_trend_hit_filter
        (run_id, frequency, pivot_type, status, observed_at, id),
    KEY ix_pair_trend_hit_symbol
        (symbol, observed_at, frequency),
    KEY ix_pair_trend_hit_pair
        (pair_code, pair_kind, pivot_type, observed_at),
    CONSTRAINT fk_pair_trend_hit_run
        FOREIGN KEY (run_id) REFERENCES pair_trend_backtest_run(id)
        ON DELETE CASCADE,
    CONSTRAINT fk_pair_trend_hit_event
        FOREIGN KEY (event_id) REFERENCES pair_trend_event(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration (version, description)
VALUES ('003', 'pair-trend-v2 backtest runs, event records and complete timeframe hits')
ON DUPLICATE KEY UPDATE description=VALUES(description);

