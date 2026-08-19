USE astock_monitor;

-- 按交易日和成立阶段生成订阅池，避免扫描全部历史命中；动态DDL保证可重复执行。
SET @hot_tick_pool_index_sql = (
    SELECT IF(COUNT(*)=0,
        'ALTER TABLE pair_trend_live_hit ADD KEY ix_pair_trend_live_hit_v5_pool (trading_date,is_promotion,stage,event_id,observed_at)',
        'SELECT 1')
    FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='pair_trend_live_hit'
      AND index_name='ix_pair_trend_live_hit_v5_pool'
);
PREPARE hot_tick_pool_index_stmt FROM @hot_tick_pool_index_sql;
EXECUTE hot_tick_pool_index_stmt;
DEALLOCATE PREPARE hot_tick_pool_index_stmt;

-- V5重点Tick池：保存每天实际入选股票及入选原因，便于回溯订阅决策。
-- 同一股票无论存在多少顶部、底部和对子价位，在同一天只保存一条选择记录。
CREATE TABLE IF NOT EXISTS hot_tick_pool_snapshot (
    subscription_date DATE NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    source_trading_date DATE NOT NULL,
    pool_version CHAR(64) NOT NULL,
    source_type VARCHAR(24) NOT NULL,
    strongest_stage VARCHAR(24) NULL,
    active_level_count INT UNSIGNED NOT NULL DEFAULT 0,
    pivot_types VARCHAR(32) NULL,
    nearest_pair_price DECIMAL(20,6) NULL,
    distance_percent DECIMAL(20,10) NULL,
    latest_hit_at DATETIME(6) NULL,
    priority BIGINT NOT NULL,
    rank_no INT UNSIGNED NOT NULL,
    selected BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (subscription_date,symbol),
    KEY ix_hot_tick_pool_source
        (source_trading_date,source_type,strongest_stage,selected,rank_no),
    KEY ix_hot_tick_pool_version (pool_version,selected,rank_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration (version,description)
VALUES ('023','previous-trading-day pair based hot Tick subscription pool')
ON DUPLICATE KEY UPDATE description=VALUES(description);
