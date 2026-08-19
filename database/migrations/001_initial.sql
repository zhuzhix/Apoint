CREATE DATABASE IF NOT EXISTS astock_monitor
  CHARACTER SET utf8mb4
  COLLATE utf8mb4_0900_ai_ci;

CREATE USER IF NOT EXISTS 'astock_app'@'%' IDENTIFIED BY 'change-me';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX ON astock_monitor.* TO 'astock_app'@'%';
FLUSH PRIVILEGES;

USE astock_monitor;

CREATE TABLE IF NOT EXISTS instrument (
    id BIGINT NOT NULL AUTO_INCREMENT,
    symbol VARCHAR(32) NOT NULL,
    exchange VARCHAR(16) NOT NULL,
    name VARCHAR(128) NOT NULL,
    security_type VARCHAR(32) NOT NULL,
    price_precision TINYINT NOT NULL DEFAULT 2,
    lot_size INT NOT NULL DEFAULT 100,
    list_date DATE NULL,
    delist_date DATE NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'active',
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_instrument_symbol (symbol),
    KEY ix_instrument_exchange (exchange),
    KEY ix_instrument_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS trading_calendar (
    trading_date DATE NOT NULL,
    exchange VARCHAR(16) NOT NULL,
    is_trading_day BOOLEAN NOT NULL,
    open_time TIME NULL,
    close_time TIME NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (trading_date, exchange)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS market_session (
    id BIGINT NOT NULL AUTO_INCREMENT,
    exchange VARCHAR(16) NOT NULL,
    session_name VARCHAR(32) NOT NULL,
    start_time TIME NOT NULL,
    end_time TIME NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_market_session (exchange, session_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS instrument_status (
    symbol VARCHAR(32) NOT NULL,
    trading_date DATE NOT NULL,
    status VARCHAR(32) NOT NULL,
    reason VARCHAR(255) NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (symbol, trading_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS subscription_config (
    id BIGINT NOT NULL AUTO_INCREMENT,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    worker_hint VARCHAR(64) NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_subscription (symbol, frequency),
    KEY ix_subscription_enabled (enabled)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS collector_worker (
    worker_id VARCHAR(64) NOT NULL,
    process_id BIGINT NULL,
    status VARCHAR(32) NOT NULL,
    assigned_count INT NOT NULL DEFAULT 0,
    last_heartbeat DATETIME(6) NULL,
    last_event_time DATETIME(6) NULL,
    received_count BIGINT NOT NULL DEFAULT 0,
    published_count BIGINT NOT NULL DEFAULT 0,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (worker_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS collector_assignment (
    assignment_version VARCHAR(64) NOT NULL,
    worker_id VARCHAR(64) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'active',
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (assignment_version, worker_id, symbol, frequency),
    KEY ix_assignment_symbol (symbol, frequency, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS quote_tick (
    id BIGINT NOT NULL AUTO_INCREMENT,
    event_id VARCHAR(128) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    trading_date DATE NOT NULL,
    event_time DATETIME(6) NOT NULL,
    receive_time DATETIME(6) NOT NULL,
    price DECIMAL(18,6) NOT NULL,
    pre_close DECIMAL(18,6) NULL,
    cum_volume BIGINT NULL,
    cum_amount DECIMAL(24,4) NULL,
    last_volume BIGINT NULL,
    last_amount DECIMAL(24,4) NULL,
    bid_price_1 DECIMAL(18,6) NULL,
    bid_volume_1 BIGINT NULL,
    ask_price_1 DECIMAL(18,6) NULL,
    ask_volume_1 BIGINT NULL,
    source VARCHAR(32) NOT NULL,
    worker_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id, trading_date),
    UNIQUE KEY uk_quote_tick_event (event_id, trading_date),
    KEY ix_quote_tick_symbol_time (symbol, event_time),
    KEY ix_quote_tick_trading_date (trading_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
PARTITION BY RANGE COLUMNS(trading_date) (
    PARTITION p202608 VALUES LESS THAN ('2026-09-01'),
    PARTITION pmax VALUES LESS THAN (MAXVALUE)
);

CREATE TABLE IF NOT EXISTS quote_bar (
    id BIGINT NOT NULL AUTO_INCREMENT,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    trading_date DATE NOT NULL,
    bob DATETIME(6) NOT NULL,
    eob DATETIME(6) NOT NULL,
    open_price DECIMAL(18,6) NOT NULL,
    high_price DECIMAL(18,6) NOT NULL,
    low_price DECIMAL(18,6) NOT NULL,
    close_price DECIMAL(18,6) NOT NULL,
    pre_close DECIMAL(18,6) NULL,
    volume BIGINT NULL,
    amount DECIMAL(24,4) NULL,
    source VARCHAR(32) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_quote_bar (symbol, frequency, eob),
    KEY ix_quote_bar_symbol_time (symbol, frequency, eob),
    KEY ix_quote_bar_trading_date (trading_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS data_quality_issue (
    id BIGINT NOT NULL AUTO_INCREMENT,
    issue_type VARCHAR(64) NOT NULL,
    symbol VARCHAR(32) NULL,
    worker_id VARCHAR(64) NULL,
    event_time DATETIME(6) NULL,
    severity VARCHAR(16) NOT NULL,
    message VARCHAR(1000) NOT NULL,
    details JSON NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    resolved_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    KEY ix_quality_issue_open (severity, resolved_at),
    KEY ix_quality_issue_symbol (symbol, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS ingest_failure (
    id BIGINT NOT NULL AUTO_INCREMENT,
    stream_key VARCHAR(255) NOT NULL,
    message_id VARCHAR(64) NOT NULL,
    event_id VARCHAR(128) NULL,
    payload JSON NOT NULL,
    error_message VARCHAR(1000) NOT NULL,
    retry_count INT NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    last_retry_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_ingest_failure_message (stream_key, message_id),
    KEY ix_ingest_failure_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
