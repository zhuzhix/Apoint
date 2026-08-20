USE astock_monitor;

-- 成立事件次一交易日价格验证。5 分钟 K 线只在 WebAPI 会话内使用，
-- 正式库仅保存验证结果、运行审计和发生修订时的前镜像。
DROP PROCEDURE IF EXISTS astock_add_next_day_validation_columns;
DELIMITER $$
CREATE PROCEDURE astock_add_next_day_validation_columns()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='next_day_validation_date') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN next_day_validation_date DATE NULL AFTER wave_revision;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='next_day_validation_status') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN next_day_validation_status VARCHAR(24) NULL AFTER next_day_validation_date;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='next_day_observed_extreme_price') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN next_day_observed_extreme_price DECIMAL(20,6) NULL AFTER next_day_validation_status;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='next_day_breached_at') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN next_day_breached_at DATETIME(6) NULL AFTER next_day_observed_extreme_price;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='next_day_breach_price') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN next_day_breach_price DECIMAL(20,6) NULL AFTER next_day_breached_at;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='next_day_validation_checked_at') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN next_day_validation_checked_at DATETIME(6) NULL AFTER next_day_breach_price;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='next_day_validation_source_hash') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN next_day_validation_source_hash CHAR(64) NULL AFTER next_day_validation_checked_at;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND index_name='ix_pair_trend_live_established_validation') THEN
        ALTER TABLE pair_trend_live_event ADD KEY ix_pair_trend_live_established_validation (algorithm_version,established_at,id);
    END IF;
END$$
DELIMITER ;
CALL astock_add_next_day_validation_columns();
DROP PROCEDURE astock_add_next_day_validation_columns;

CREATE TABLE IF NOT EXISTS pair_trend_next_day_validation_run (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_key CHAR(64) NOT NULL,
    run_mode VARCHAR(16) NOT NULL,
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    apply_changes BOOLEAN NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'PREPARED',
    total_count INT UNSIGNED NOT NULL DEFAULT 0,
    completed_count INT UNSIGNED NOT NULL DEFAULT 0,
    invalidated_count INT UNSIGNED NOT NULL DEFAULT 0,
    passed_count INT UNSIGNED NOT NULL DEFAULT 0,
    no_trade_count INT UNSIGNED NOT NULL DEFAULT 0,
    not_applicable_count INT UNSIGNED NOT NULL DEFAULT 0,
    failed_count INT UNSIGNED NOT NULL DEFAULT 0,
    last_error VARCHAR(2000) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_next_day_run (run_key),
    KEY ix_pair_trend_next_day_run_status (status,id),
    CONSTRAINT chk_pair_trend_next_day_run_mode CHECK (run_mode IN ('HISTORICAL','REALTIME')),
    CONSTRAINT chk_pair_trend_next_day_run_status CHECK (status IN ('PREPARED','RUNNING','COMPLETED','COMPLETED_WITH_ERRORS','FAILED'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_trend_next_day_validation (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_id BIGINT NOT NULL,
    event_id BIGINT NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    pivot_type VARCHAR(16) NOT NULL,
    pair_price DECIMAL(20,6) NOT NULL,
    established_trading_date DATE NOT NULL,
    validation_trading_date DATE NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'PENDING',
    attempt_count TINYINT UNSIGNED NOT NULL DEFAULT 0,
    lease_token CHAR(36) NULL,
    lease_owner VARCHAR(128) NULL,
    lease_expires_at DATETIME(6) NULL,
    observed_extreme_price DECIMAL(20,6) NULL,
    breached_at DATETIME(6) NULL,
    breach_price DECIMAL(20,6) NULL,
    source_input_hash CHAR(64) NULL,
    bar_count SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    verified_missing_count SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    last_error VARCHAR(2000) NULL,
    completed_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_next_day_run_event (run_id,event_id),
    KEY ix_pair_trend_next_day_claim (run_id,status,validation_trading_date,symbol,id),
    KEY ix_pair_trend_next_day_event (event_id,completed_at,id),
    CONSTRAINT fk_pair_trend_next_day_run FOREIGN KEY (run_id) REFERENCES pair_trend_next_day_validation_run(id) ON DELETE CASCADE,
    CONSTRAINT fk_pair_trend_next_day_event FOREIGN KEY (event_id) REFERENCES pair_trend_live_event(id) ON DELETE CASCADE,
    CONSTRAINT chk_pair_trend_next_day_status CHECK (status IN ('PENDING','RETRY','LEASED','PASSED','INVALIDATED','NO_TRADE','NOT_APPLICABLE','FAILED')),
    CONSTRAINT chk_pair_trend_next_day_pivot CHECK (pivot_type IN ('TOP','BOTTOM'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_trend_next_day_validation_change (
    id BIGINT NOT NULL AUTO_INCREMENT,
    validation_id BIGINT NOT NULL,
    event_id BIGINT NOT NULL,
    previous_stage VARCHAR(24) NOT NULL,
    previous_is_active BOOLEAN NOT NULL,
    previous_invalidated_at DATETIME(6) NULL,
    previous_invalidated_price DECIMAL(20,6) NULL,
    previous_invalidation_reason VARCHAR(64) NULL,
    previous_last_transition_at DATETIME(6) NULL,
    previous_event_revision INT UNSIGNED NOT NULL,
    previous_content_hash CHAR(64) NOT NULL,
    previous_summary_json JSON NOT NULL,
    applied_breached_at DATETIME(6) NOT NULL,
    applied_breach_price DECIMAL(20,6) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    rolled_back_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_next_day_change (validation_id),
    KEY ix_pair_trend_next_day_change_event (event_id,id),
    CONSTRAINT fk_pair_trend_next_day_change_validation FOREIGN KEY (validation_id) REFERENCES pair_trend_next_day_validation(id) ON DELETE CASCADE,
    CONSTRAINT fk_pair_trend_next_day_change_event FOREIGN KEY (event_id) REFERENCES pair_trend_live_event(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration(version,description)
VALUES ('036','established pair next-trading-day price validation and historical audit')
ON DUPLICATE KEY UPDATE description=VALUES(description);
