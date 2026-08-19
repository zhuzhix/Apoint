USE astock_monitor;

SET @has_run_mode = (
    SELECT COUNT(*)
    FROM information_schema.columns
    WHERE table_schema = DATABASE()
      AND table_name = 'pair_trend_backtest_run'
      AND column_name = 'run_mode'
);
SET @add_run_mode = IF(
    @has_run_mode = 0,
    'ALTER TABLE pair_trend_backtest_run ADD COLUMN run_mode VARCHAR(24) NOT NULL DEFAULT ''historical'' AFTER algorithm_version',
    'SELECT 1'
);
PREPARE pair_run_stmt FROM @add_run_mode;
EXECUTE pair_run_stmt;
DEALLOCATE PREPARE pair_run_stmt;

SET @has_data_source = (
    SELECT COUNT(*)
    FROM information_schema.columns
    WHERE table_schema = DATABASE()
      AND table_name = 'pair_trend_backtest_run'
      AND column_name = 'data_source'
);
SET @add_data_source = IF(
    @has_data_source = 0,
    'ALTER TABLE pair_trend_backtest_run ADD COLUMN data_source VARCHAR(64) NOT NULL DEFAULT ''dongcai-gm'' AFTER run_mode',
    'SELECT 1'
);
PREPARE pair_source_stmt FROM @add_data_source;
EXECUTE pair_source_stmt;
DEALLOCATE PREPARE pair_source_stmt;

SET @has_notes = (
    SELECT COUNT(*)
    FROM information_schema.columns
    WHERE table_schema = DATABASE()
      AND table_name = 'pair_trend_backtest_run'
      AND column_name = 'notes'
);
SET @add_notes = IF(
    @has_notes = 0,
    'ALTER TABLE pair_trend_backtest_run ADD COLUMN notes VARCHAR(1000) NULL AFTER data_source',
    'SELECT 1'
);
PREPARE pair_notes_stmt FROM @add_notes;
EXECUTE pair_notes_stmt;
DEALLOCATE PREPARE pair_notes_stmt;

INSERT INTO schema_migration (version, description)
VALUES ('004', 'pair trend backtest run provenance')
ON DUPLICATE KEY UPDATE description=VALUES(description);
