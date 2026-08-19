USE astock_monitor;

CREATE TABLE IF NOT EXISTS history_source_exception (
    id BIGINT NOT NULL AUTO_INCREMENT,
    batch_id BIGINT NOT NULL,
    scope_key VARCHAR(160) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'open',
    reason VARCHAR(1000) NOT NULL,
    retry_count INT NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    resolved_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_history_source_exception
        (batch_id, symbol, frequency, date_from, date_to),
    KEY ix_history_source_exception_status (status, updated_at),
    KEY ix_history_source_exception_symbol (symbol, frequency, date_from)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration (version, description)
VALUES ('020', 'auditable quarantines for unavailable official history ranges')
ON DUPLICATE KEY UPDATE description=VALUES(description);
