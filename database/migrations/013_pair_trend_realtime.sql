USE astock_monitor;

-- 长期实时对子事件与历史回测完全分离，不伪造 backtest run_id。
CREATE TABLE IF NOT EXISTS pair_trend_live_event (
    id BIGINT NOT NULL AUTO_INCREMENT,
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
    retracted_hit_count INT UNSIGNED NOT NULL DEFAULT 0,
    round_00_hit_count INT UNSIGNED NOT NULL DEFAULT 0,
    double_digit_hit_count INT UNSIGNED NOT NULL DEFAULT 0,
    score DECIMAL(10,6) NOT NULL DEFAULT 0,
    max_trend_strength DECIMAL(20,8) NOT NULL DEFAULT 0,
    algorithm_version VARCHAR(32) NOT NULL,
    event_revision INT UNSIGNED NOT NULL DEFAULT 0,
    content_hash CHAR(64) NOT NULL,
    last_source_event_id VARCHAR(160) NOT NULL,
    summary_json JSON NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_live_event (event_key),
    KEY ix_pair_trend_live_filter (pivot_type, status, last_seen_at, id),
    KEY ix_pair_trend_live_symbol (symbol, last_seen_at, pivot_type),
    KEY ix_pair_trend_live_score (score, last_seen_at, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_trend_live_hit (
    id BIGINT NOT NULL AUTO_INCREMENT,
    event_id BIGINT NULL,
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
    price_ticks BIGINT NOT NULL,
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
    source_revision INT UNSIGNED NOT NULL DEFAULT 0,
    source_row_hash CHAR(64) NOT NULL,
    source_event_id VARCHAR(160) NOT NULL,
    algorithm_version VARCHAR(32) NOT NULL,
    details_json JSON NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_live_hit_business
        (symbol, frequency, eob, pivot_type, algorithm_version),
    UNIQUE KEY uk_pair_trend_live_hit_key (hit_key),
    KEY ix_pair_trend_live_hit_event (event_id, observed_at, id),
    KEY ix_pair_trend_live_hit_filter
        (frequency, pivot_type, status, observed_at, id),
    KEY ix_pair_trend_live_hit_symbol (symbol, observed_at, frequency),
    CONSTRAINT fk_pair_trend_live_hit_event
        FOREIGN KEY (event_id) REFERENCES pair_trend_live_event(id)
        ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_trend_event_outbox (
    id BIGINT NOT NULL AUTO_INCREMENT,
    outbox_event_id VARCHAR(160) NOT NULL,
    event_id BIGINT NOT NULL,
    event_key CHAR(64) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    lifecycle_type VARCHAR(32) NOT NULL,
    event_revision INT UNSIGNED NOT NULL,
    payload JSON NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    lease_owner VARCHAR(128) NULL,
    lease_expires_at DATETIME(6) NULL,
    attempt_count INT NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(6) NULL,
    stream_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    published_at DATETIME(6) NULL,
    last_error VARCHAR(2000) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_event_outbox (outbox_event_id),
    KEY ix_pair_trend_event_publish (status, lease_expires_at, next_attempt_at, id),
    CONSTRAINT fk_pair_trend_event_outbox_event
        FOREIGN KEY (event_id) REFERENCES pair_trend_live_event(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_trend_consumer_checkpoint (
    shard SMALLINT UNSIGNED NOT NULL,
    stream_key VARCHAR(160) NOT NULL,
    last_message_id VARCHAR(64) NOT NULL,
    last_source_event_id VARCHAR(160) NOT NULL,
    last_success_at DATETIME(6) NOT NULL,
    processed_count BIGINT UNSIGNED NOT NULL DEFAULT 0,
    failure_count BIGINT UNSIGNED NOT NULL DEFAULT 0,
    last_error VARCHAR(2000) NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (shard)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_trend_processed_event (
    source_event_id VARCHAR(160) NOT NULL,
    shard SMALLINT UNSIGNED NOT NULL,
    stream_message_id VARCHAR(64) NOT NULL,
    processed_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (source_event_id),
    KEY ix_pair_trend_processed_time (processed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO data_retention_policy
    (dataset_name, retention_mode, cutoff_rule, archive_before_purge)
VALUES
    ('pair_trend_live_event', 'keep_forever',
     'Keep live pair trend event history unless explicitly archived', TRUE),
    ('pair_trend_live_hit', 'annual_partition_archive',
     'Archive old live hit details only after event summaries are verified', TRUE),
    ('pair_trend_event_outbox', 'rolling_purge',
     'Keep published pair event audit for at least 12 months', TRUE),
    ('pair_trend_consumer_checkpoint', 'keep_forever',
     'Keep latest shard progress and health state', FALSE),
    ('pair_trend_processed_event', 'rolling_purge',
     'Keep source event deduplication at least as long as the Bar event stream', FALSE)
ON DUPLICATE KEY UPDATE
    retention_mode=VALUES(retention_mode),
    cutoff_rule=VALUES(cutoff_rule),
    archive_before_purge=VALUES(archive_before_purge);

INSERT INTO schema_migration (version, description)
VALUES ('013', 'realtime pair trend events, hits, outbox and shard checkpoints')
ON DUPLICATE KEY UPDATE description=VALUES(description);
