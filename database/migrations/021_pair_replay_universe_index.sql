USE astock_monitor;

-- Full-market pair replay selects eligible symbols by a date range.  The
-- original index starts with trading_date and still requires a temporary
-- grouping by symbol; this covering index keeps that operation index-only.
SET @has_index = (
    SELECT COUNT(*) FROM information_schema.statistics
    WHERE table_schema=DATABASE()
      AND table_name='instrument_daily_status'
      AND index_name='ix_instrument_daily_eligible_symbol'
);
SET @ddl = IF(
    @has_index=0,
    'CREATE INDEX ix_instrument_daily_eligible_symbol ON instrument_daily_status (trading_date, is_eligible, symbol)',
    'SELECT 1'
);
PREPARE statement FROM @ddl;
EXECUTE statement;
DEALLOCATE PREPARE statement;

INSERT INTO schema_migration (version, description)
VALUES ('021', 'covering index for point-in-time pair replay universe')
ON DUPLICATE KEY UPDATE description=VALUES(description);
