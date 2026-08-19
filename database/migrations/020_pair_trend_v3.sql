USE astock_monitor;

-- pair-trend-v3: exact-price survival and cross-timeframe stage state.
ALTER TABLE pair_trend_event
    ADD COLUMN price_ticks BIGINT NOT NULL DEFAULT 0 AFTER latest_pair_price,
    ADD COLUMN stage VARCHAR(24) NOT NULL DEFAULT 'DISCOVERED' AFTER algorithm_version,
    ADD COLUMN generation INT UNSIGNED NOT NULL DEFAULT 1 AFTER stage,
    ADD COLUMN is_active BOOLEAN NOT NULL DEFAULT TRUE AFTER generation,
    ADD COLUMN discovered_at DATETIME(6) NULL AFTER is_active,
    ADD COLUMN observed_at DATETIME(6) NULL AFTER discovered_at,
    ADD COLUMN focused_at DATETIME(6) NULL AFTER observed_at,
    ADD COLUMN established_at DATETIME(6) NULL AFTER focused_at,
    ADD COLUMN invalidated_at DATETIME(6) NULL AFTER established_at,
    ADD COLUMN invalidated_price DECIMAL(20,6) NULL AFTER invalidated_at,
    ADD COLUMN invalidation_reason VARCHAR(64) NULL AFTER invalidated_price,
    ADD COLUMN root_5m_bob DATETIME(6) NULL AFTER invalidation_reason,
    ADD COLUMN root_5m_eob DATETIME(6) NULL AFTER root_5m_bob,
    ADD COLUMN last_transition_at DATETIME(6) NULL AFTER root_5m_eob,
    ADD KEY ix_pair_trend_v3_level
        (run_id,symbol,pivot_type,price_ticks,is_active,stage),
    ADD KEY ix_pair_trend_event_run_symbol (run_id,symbol);

ALTER TABLE pair_trend_hit
    ADD COLUMN price_ticks BIGINT NOT NULL DEFAULT 0 AFTER pair_price,
    ADD COLUMN stage VARCHAR(24) NOT NULL DEFAULT 'DISCOVERED' AFTER algorithm_version,
    ADD COLUMN is_promotion BOOLEAN NOT NULL DEFAULT FALSE AFTER stage,
    ADD KEY ix_pair_trend_hit_v3_stage (run_id,stage,frequency,observed_at);

CREATE TABLE pair_trend_lifecycle (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_id BIGINT NOT NULL,
    event_id BIGINT NOT NULL,
    lifecycle_key CHAR(64) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    from_stage VARCHAR(24) NULL,
    to_stage VARCHAR(24) NOT NULL,
    occurred_at DATETIME(6) NOT NULL,
    trigger_frequency VARCHAR(16) NOT NULL,
    trigger_price DECIMAL(20,6) NOT NULL,
    reason VARCHAR(64) NOT NULL,
    source_row_hash CHAR(64) NOT NULL,
    should_notify BOOLEAN NOT NULL DEFAULT FALSE,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_lifecycle (run_id,lifecycle_key),
    KEY ix_pair_trend_lifecycle_event (event_id,occurred_at,id),
    KEY ix_pair_trend_lifecycle_stage (run_id,to_stage,occurred_at,id),
    CONSTRAINT fk_pair_trend_lifecycle_run
        FOREIGN KEY (run_id) REFERENCES pair_trend_backtest_run(id) ON DELETE CASCADE,
    CONSTRAINT fk_pair_trend_lifecycle_event
        FOREIGN KEY (event_id) REFERENCES pair_trend_event(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

ALTER TABLE pair_trend_live_event
    ADD COLUMN price_ticks BIGINT NOT NULL DEFAULT 0 AFTER latest_pair_price,
    ADD COLUMN stage VARCHAR(24) NOT NULL DEFAULT 'DISCOVERED' AFTER algorithm_version,
    ADD COLUMN generation INT UNSIGNED NOT NULL DEFAULT 1 AFTER stage,
    ADD COLUMN is_active BOOLEAN NOT NULL DEFAULT TRUE AFTER generation,
    ADD COLUMN discovered_at DATETIME(6) NULL AFTER is_active,
    ADD COLUMN observed_at DATETIME(6) NULL AFTER discovered_at,
    ADD COLUMN focused_at DATETIME(6) NULL AFTER observed_at,
    ADD COLUMN established_at DATETIME(6) NULL AFTER focused_at,
    ADD COLUMN invalidated_at DATETIME(6) NULL AFTER established_at,
    ADD COLUMN invalidated_price DECIMAL(20,6) NULL AFTER invalidated_at,
    ADD COLUMN invalidation_reason VARCHAR(64) NULL AFTER invalidated_price,
    ADD COLUMN root_5m_bob DATETIME(6) NULL AFTER invalidation_reason,
    ADD COLUMN root_5m_eob DATETIME(6) NULL AFTER root_5m_bob,
    ADD COLUMN last_transition_at DATETIME(6) NULL AFTER root_5m_eob,
    ADD KEY ix_pair_trend_live_v3_level
        (symbol,pivot_type,price_ticks,is_active,stage);

ALTER TABLE pair_trend_live_hit
    ADD COLUMN stage VARCHAR(24) NOT NULL DEFAULT 'DISCOVERED' AFTER algorithm_version,
    ADD COLUMN is_promotion BOOLEAN NOT NULL DEFAULT FALSE AFTER stage,
    ADD KEY ix_pair_trend_live_hit_v3_stage (stage,frequency,observed_at);

CREATE TABLE pair_trend_live_lifecycle (
    id BIGINT NOT NULL AUTO_INCREMENT,
    event_id BIGINT NOT NULL,
    lifecycle_key CHAR(64) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    from_stage VARCHAR(24) NULL,
    to_stage VARCHAR(24) NOT NULL,
    occurred_at DATETIME(6) NOT NULL,
    trigger_frequency VARCHAR(16) NOT NULL,
    trigger_price DECIMAL(20,6) NOT NULL,
    reason VARCHAR(64) NOT NULL,
    source_row_hash CHAR(64) NOT NULL,
    should_notify BOOLEAN NOT NULL DEFAULT FALSE,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_live_lifecycle (lifecycle_key),
    KEY ix_pair_trend_live_lifecycle_event (event_id,occurred_at,id),
    CONSTRAINT fk_pair_trend_live_lifecycle_event
        FOREIGN KEY (event_id) REFERENCES pair_trend_live_event(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration (version,description)
VALUES ('020','pair-trend-v3 exact-price survival stages and lifecycle audit')
ON DUPLICATE KEY UPDATE description=VALUES(description);
