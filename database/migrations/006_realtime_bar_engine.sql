USE astock_monitor;

-- 迁移脚本会被重复执行，因此所有 quote_bar 列变更都经过 information_schema 防重。
DROP PROCEDURE IF EXISTS add_quote_bar_column_if_missing;
DELIMITER //
CREATE PROCEDURE add_quote_bar_column_if_missing(
    IN column_name_value VARCHAR(64),
    IN column_definition_value VARCHAR(512)
)
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = DATABASE()
          AND table_name = 'quote_bar'
          AND column_name = column_name_value
    ) THEN
        SET @ddl = CONCAT(
            'ALTER TABLE quote_bar ADD COLUMN ',
            column_name_value,
            ' ',
            column_definition_value
        );
        PREPARE statement_to_run FROM @ddl;
        EXECUTE statement_to_run;
        DEALLOCATE PREPARE statement_to_run;
    END IF;
END //
DELIMITER ;

CALL add_quote_bar_column_if_missing('is_closed', 'BOOLEAN NOT NULL DEFAULT TRUE AFTER source');
CALL add_quote_bar_column_if_missing('volume_complete', 'BOOLEAN NOT NULL DEFAULT TRUE AFTER is_closed');
CALL add_quote_bar_column_if_missing('amount_complete', 'BOOLEAN NOT NULL DEFAULT TRUE AFTER volume_complete');
CALL add_quote_bar_column_if_missing('revision', 'INT NOT NULL DEFAULT 0 AFTER amount_complete');
CALL add_quote_bar_column_if_missing('official_confirmed', 'BOOLEAN NOT NULL DEFAULT FALSE AFTER revision');
CALL add_quote_bar_column_if_missing('first_tick_time', 'DATETIME(6) NULL AFTER official_confirmed');
CALL add_quote_bar_column_if_missing('last_tick_time', 'DATETIME(6) NULL AFTER first_tick_time');
CALL add_quote_bar_column_if_missing('last_bar_event_id', 'VARCHAR(128) NULL AFTER last_tick_time');
CALL add_quote_bar_column_if_missing('row_hash', 'CHAR(64) NOT NULL DEFAULT '''' AFTER last_bar_event_id');

DROP PROCEDURE IF EXISTS add_quote_bar_column_if_missing;

CREATE TABLE IF NOT EXISTS realtime_bar_event (
    id BIGINT NOT NULL AUTO_INCREMENT,
    event_id VARCHAR(128) NOT NULL,
    event_type VARCHAR(16) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    trading_date DATE NOT NULL,
    bob DATETIME(6) NOT NULL,
    eob DATETIME(6) NOT NULL,
    revision INT NOT NULL,
    row_hash CHAR(64) NOT NULL,
    cause_event_id VARCHAR(128) NULL,
    payload JSON NOT NULL,
    occurred_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_realtime_bar_event_id (event_id),
    KEY ix_realtime_bar_event_symbol (symbol, frequency, eob),
    KEY ix_realtime_bar_event_time (occurred_at, event_type)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO data_retention_policy
    (dataset_name, retention_mode, cutoff_rule, archive_before_purge)
VALUES
    ('quote_bar', 'annual_partition_archive',
     'In January archive and purge intraday bars before July 1 of previous year; keep daily bars', TRUE),
    ('realtime_bar_event', 'rolling_purge',
     'Keep lifecycle audit events for 12 months after durable bar validation', TRUE)
ON DUPLICATE KEY UPDATE
    retention_mode=VALUES(retention_mode),
    cutoff_rule=VALUES(cutoff_rule),
    archive_before_purge=VALUES(archive_before_purge);

INSERT INTO schema_migration (version, description)
VALUES ('006', 'realtime Tick-to-bar engine lifecycle and revision persistence')
ON DUPLICATE KEY UPDATE description=VALUES(description);
