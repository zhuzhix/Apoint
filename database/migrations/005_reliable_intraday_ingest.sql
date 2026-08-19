USE astock_monitor;

-- The migration runner reapplies all files, so schema changes are guarded by
-- information_schema checks instead of assuming a one-shot migration tool.
DROP PROCEDURE IF EXISTS add_quote_tick_column_if_missing;
DELIMITER //
CREATE PROCEDURE add_quote_tick_column_if_missing(
    IN column_name_value VARCHAR(64),
    IN column_definition_value VARCHAR(255)
)
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = DATABASE()
          AND table_name = 'quote_tick'
          AND column_name = column_name_value
    ) THEN
        SET @ddl = CONCAT(
            'ALTER TABLE quote_tick ADD COLUMN ',
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

CALL add_quote_tick_column_if_missing('session_id', 'VARCHAR(64) NULL AFTER worker_id');
CALL add_quote_tick_column_if_missing('worker_sequence', 'BIGINT NOT NULL DEFAULT 0 AFTER session_id');
CALL add_quote_tick_column_if_missing('server_receive_time', 'DATETIME(6) NULL AFTER receive_time');

DROP PROCEDURE IF EXISTS add_quote_tick_column_if_missing;

CREATE TABLE IF NOT EXISTS market_ingest_checkpoint (
    stream_key VARCHAR(255) NOT NULL,
    consumer_group VARCHAR(128) NOT NULL,
    last_stream_id VARCHAR(64) NOT NULL,
    committed_count BIGINT NOT NULL DEFAULT 0,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (stream_key, consumer_group)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
