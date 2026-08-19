USE astock_monitor;

DROP PROCEDURE IF EXISTS add_universe_v2_column;
DELIMITER //
CREATE PROCEDURE add_universe_v2_column(
    IN column_name_value VARCHAR(64),
    IN column_definition_value VARCHAR(512)
)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema=DATABASE()
          AND table_name='instrument_daily_status'
          AND column_name=column_name_value
    ) THEN
        SET @ddl=CONCAT(
            'ALTER TABLE instrument_daily_status ADD COLUMN `',
            column_name_value, '` ', column_definition_value
        );
        PREPARE statement_to_run FROM @ddl;
        EXECUTE statement_to_run;
        DEALLOCATE PREPARE statement_to_run;
    END IF;
END //
DELIMITER ;

CALL add_universe_v2_column('is_a_share',
    'BOOLEAN NOT NULL DEFAULT FALSE AFTER is_suspended');
CALL add_universe_v2_column('status_source',
    'VARCHAR(64) NOT NULL DEFAULT ''unknown'' AFTER is_eligible');
CALL add_universe_v2_column('status_quality',
    'VARCHAR(32) NOT NULL DEFAULT ''unknown'' AFTER status_source');
CALL add_universe_v2_column('exclusion_reason',
    'VARCHAR(64) NULL AFTER status_quality');
DROP PROCEDURE IF EXISTS add_universe_v2_column;

UPDATE instrument_daily_status
SET is_a_share=(
        symbol REGEXP '^SHSE\\.(600|601|603|605|688)[0-9]{3}$'
        OR symbol REGEXP '^SZSE\\.(000|001|002|003|300|301)[0-9]{3}$'
    ),
    status_source=COALESCE(
        JSON_UNQUOTE(JSON_EXTRACT(raw_attributes,'$._universe_adapter')),
        'legacy_unknown'),
    status_quality=CASE
        WHEN JSON_UNQUOTE(JSON_EXTRACT(raw_attributes,'$._universe_adapter'))
             ='get_instruments-current-snapshot'
        THEN 'estimated_current_snapshot'
        WHEN JSON_UNQUOTE(JSON_EXTRACT(raw_attributes,'$._universe_adapter'))='get_symbols'
        THEN 'historical_exact'
        ELSE 'unknown'
    END;

UPDATE instrument_daily_status
SET is_eligible=FALSE,
    exclusion_reason=CASE
        WHEN is_a_share=FALSE THEN 'NOT_A_SHARE'
        WHEN is_st=TRUE THEN 'ST'
        ELSE exclusion_reason
    END
WHERE is_a_share=FALSE OR is_st=TRUE;

INSERT INTO schema_migration (version, description)
VALUES ('014', 'strict mainland A-share universe and historical status quality provenance')
ON DUPLICATE KEY UPDATE description=VALUES(description);
