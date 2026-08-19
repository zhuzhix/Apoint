USE astock_monitor;

CREATE TABLE IF NOT EXISTS strategy_definition (
    strategy_code VARCHAR(96) NOT NULL,
    name VARCHAR(128) NOT NULL,
    scan_profile VARCHAR(24) NOT NULL,
    current_version VARCHAR(32) NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    required_frequencies JSON NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (strategy_code),
    KEY ix_strategy_definition_enabled (enabled, scan_profile)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_version (
    id BIGINT NOT NULL AUTO_INCREMENT,
    strategy_code VARCHAR(96) NOT NULL,
    version VARCHAR(32) NOT NULL,
    rule_summary VARCHAR(2000) NOT NULL,
    parameter_json JSON NOT NULL,
    data_requirements JSON NOT NULL,
    code_hash CHAR(64) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_strategy_version (strategy_code, version),
    KEY ix_strategy_version_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_scan_run (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_key VARCHAR(190) NOT NULL,
    scan_profile VARCHAR(24) NOT NULL,
    trigger_type VARCHAR(32) NOT NULL,
    trading_date DATE NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'running',
    requested_symbols INT NOT NULL DEFAULT 0,
    completed_symbols INT NOT NULL DEFAULT 0,
    qualified_signals INT NOT NULL DEFAULT 0,
    error_message VARCHAR(2000) NULL,
    started_at DATETIME(6) NOT NULL,
    finished_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_strategy_scan_run_key (run_key),
    KEY ix_strategy_scan_run_date (trading_date, scan_profile, started_at),
    KEY ix_strategy_scan_run_status (status, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_signal_event (
    id BIGINT NOT NULL AUTO_INCREMENT,
    event_id CHAR(64) NOT NULL,
    previous_event_id CHAR(64) NULL,
    run_id BIGINT NOT NULL,
    strategy_code VARCHAR(96) NOT NULL,
    strategy_version VARCHAR(32) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    trading_date DATE NOT NULL,
    observed_at DATETIME(6) NOT NULL,
    event_type VARCHAR(24) NOT NULL,
    action VARCHAR(24) NOT NULL,
    confidence VARCHAR(16) NOT NULL,
    score DECIMAL(8,2) NOT NULL,
    hit_price DECIMAL(20,6) NOT NULL,
    stop_reference DECIMAL(20,6) NULL,
    target_reference DECIMAL(20,6) NULL,
    passed_conditions JSON NOT NULL,
    failed_conditions JSON NOT NULL,
    feature_snapshot JSON NOT NULL,
    parameter_snapshot JSON NOT NULL,
    source_watermark VARCHAR(255) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_strategy_signal_event (event_id),
    KEY ix_strategy_signal_date (trading_date, strategy_code, event_type),
    KEY ix_strategy_signal_symbol (symbol, trading_date, observed_at),
    KEY ix_strategy_signal_run (run_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_opportunity (
    id BIGINT NOT NULL AUTO_INCREMENT,
    trading_date DATE NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    level VARCHAR(16) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'active',
    primary_strategy_code VARCHAR(96) NOT NULL,
    highest_score DECIMAL(8,2) NOT NULL,
    strategy_count SMALLINT UNSIGNED NOT NULL DEFAULT 1,
    first_seen_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NOT NULL,
    weakened_at DATETIME(6) NULL,
    expired_at DATETIME(6) NULL,
    latest_event_id CHAR(64) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_strategy_opportunity (trading_date, symbol),
    KEY ix_strategy_opportunity_page (trading_date, status, level, highest_score, last_seen_at),
    KEY ix_strategy_opportunity_symbol (symbol, last_seen_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_opportunity_detail (
    opportunity_id BIGINT NOT NULL,
    strategy_code VARCHAR(96) NOT NULL,
    strategy_version VARCHAR(32) NOT NULL,
    action VARCHAR(24) NOT NULL,
    confidence VARCHAR(16) NOT NULL,
    current_score DECIMAL(8,2) NOT NULL,
    highest_score DECIMAL(8,2) NOT NULL,
    hit_count INT UNSIGNED NOT NULL DEFAULT 1,
    first_seen_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NOT NULL,
    latest_event_id CHAR(64) NOT NULL,
    source_watermark VARCHAR(255) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'active',
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (opportunity_id, strategy_code),
    KEY ix_strategy_detail_latest (strategy_code, status, last_seen_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_filter_funnel (
    run_id BIGINT NOT NULL,
    strategy_code VARCHAR(96) NOT NULL,
    step_code VARCHAR(96) NOT NULL,
    step_name VARCHAR(255) NOT NULL,
    evaluated_count INT UNSIGNED NOT NULL DEFAULT 0,
    passed_count INT UNSIGNED NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (run_id, strategy_code, step_code),
    KEY ix_strategy_funnel_strategy (strategy_code, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_scan_checkpoint (
    scope_key VARCHAR(190) NOT NULL,
    checkpoint_value VARCHAR(512) NOT NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (scope_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_event_outbox (
    id BIGINT NOT NULL AUTO_INCREMENT,
    event_id CHAR(64) NOT NULL,
    payload JSON NOT NULL,
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    last_error VARCHAR(1000) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    published_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_strategy_outbox_event (event_id),
    KEY ix_strategy_outbox_pending (published_at, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO strategy_definition
    (strategy_code, name, scan_profile, current_version, enabled, required_frequencies)
VALUES
    ('intraday-vwap-volume-resonance', '分时VWAP量价共振', 'fast', '1.0.0', TRUE, JSON_ARRAY('1m')),
    ('gap-recovery-vwap-restart', '低开高走VWAP再启动', 'fast', '1.0.0', TRUE, JSON_ARRAY('1m')),
    ('platform-volume-breakout', '平台放量突破', 'event', '1.0.0', TRUE, JSON_ARRAY('1d','1w','30m')),
    ('moving-average-pullback-restart', '均线回踩再启动', 'observe', '1.0.0', TRUE, JSON_ARRAY('1d')),
    ('long-support-rebound', '下跌浪二次探底反弹', 'event', '1.0.0', TRUE, JSON_ARRAY('1d','30m')),
    ('strong-trend-continuation', '强势趋势延续', 'observe', '1.0.0', TRUE, JSON_ARRAY('1d')),
    ('counter-trend-strength', '逆势走强', 'observe', '1.0.0', TRUE, JSON_ARRAY('1d')),
    ('strong-repair-rebound', '强修复反弹', 'observe', '1.0.0', TRUE, JSON_ARRAY('1d','1m'))
ON DUPLICATE KEY UPDATE
    name=VALUES(name), scan_profile=VALUES(scan_profile), current_version=VALUES(current_version),
    required_frequencies=VALUES(required_frequencies), updated_at=UTC_TIMESTAMP(6);

INSERT INTO strategy_version
    (strategy_code, version, rule_summary, parameter_json, data_requirements, code_hash)
SELECT strategy_code, current_version,
       CONCAT(name, '：仅使用个股价格、成交量、成交额、VWAP、均线、平台及多周期K线确认。'),
       JSON_OBJECT('scoreScale','0-100','minimumQualifiedScore',75),
       required_frequencies,
       SHA2(CONCAT(strategy_code, ':', current_version, ':price-volume-only'), 256)
FROM strategy_definition
ON DUPLICATE KEY UPDATE
    rule_summary=VALUES(rule_summary), parameter_json=VALUES(parameter_json),
    data_requirements=VALUES(data_requirements), code_hash=VALUES(code_hash);

INSERT INTO data_retention_policy
    (dataset_name, retention_mode, cutoff_rule, archive_before_purge)
VALUES
    ('strategy_signal_event', 'keep_forever', 'Keep immutable strategy evidence', TRUE),
    ('strategy_scan_run', 'rolling_purge', 'Keep detailed scan runs for 24 months', TRUE),
    ('strategy_event_outbox', 'rolling_purge', 'Purge published events after 90 days', TRUE)
ON DUPLICATE KEY UPDATE
    retention_mode=VALUES(retention_mode), cutoff_rule=VALUES(cutoff_rule),
    archive_before_purge=VALUES(archive_before_purge);

INSERT INTO schema_migration (version, description)
VALUES ('008', 'independent strategy scanner definitions, immutable signals, opportunities and outbox')
ON DUPLICATE KEY UPDATE description=VALUES(description);
