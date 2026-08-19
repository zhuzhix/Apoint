USE astock_monitor;

-- WebAPI 只有看到当天 completed 凭证，并确认凭证计数与当日状态表一致后，
-- 才允许生成 K 线采集计划。该表不允许保存上一交易日兜底标记。
CREATE TABLE IF NOT EXISTS authoritative_universe_sync (
    trading_date DATE NOT NULL,
    status VARCHAR(24) NOT NULL,
    is_trading_day BOOLEAN NOT NULL,
    collector_id VARCHAR(96) NOT NULL,
    source VARCHAR(32) NOT NULL,
    source_updated_at DATETIME(6) NOT NULL,
    total_symbol_count INT NOT NULL,
    eligible_symbol_count INT NOT NULL,
    universe_version VARCHAR(64) NOT NULL,
    payload_hash CHAR(64) NOT NULL,
    synced_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (trading_date),
    KEY ix_authoritative_universe_status (status,trading_date),
    KEY ix_authoritative_universe_hash (payload_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration(version,description)
VALUES ('028','authoritative same-day trading calendar and A-share universe sync gate')
ON DUPLICATE KEY UPDATE description=VALUES(description);
