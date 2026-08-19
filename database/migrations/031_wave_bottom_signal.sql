USE astock_monitor;

-- The wave score is a point-in-time supplement evaluated when a BOTTOM event
-- first reaches FOCUS. It must not replace pair-trend-v3 stage or score.
-- MySQL 8.4 does not accept MariaDB's ADD COLUMN IF NOT EXISTS syntax.
-- A short-lived procedure keeps the migration repeatable while preserving the
-- exact column order required by existing deployments.
DROP PROCEDURE IF EXISTS astock_add_wave_bottom_columns;
DELIMITER $$
CREATE PROCEDURE astock_add_wave_bottom_columns()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='wave_calculation_status') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN wave_calculation_status VARCHAR(24) NOT NULL DEFAULT 'NOT_ELIGIBLE' AFTER last_transition_at;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='wave_signal') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN wave_signal VARCHAR(24) NULL AFTER wave_calculation_status;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='wave_score') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN wave_score TINYINT UNSIGNED NULL AFTER wave_signal;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='wave_evaluated_at') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN wave_evaluated_at DATETIME(6) NULL AFTER wave_score;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='wave_data_as_of') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN wave_data_as_of DATETIME(6) NULL AFTER wave_evaluated_at;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='wave_algorithm_version') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN wave_algorithm_version VARCHAR(32) NULL AFTER wave_data_as_of;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='wave_input_hash') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN wave_input_hash CHAR(64) NULL AFTER wave_algorithm_version;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='wave_components') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN wave_components JSON NULL AFTER wave_input_hash;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event' AND column_name='wave_revision') THEN
        ALTER TABLE pair_trend_live_event ADD COLUMN wave_revision INT UNSIGNED NOT NULL DEFAULT 0 AFTER wave_components;
    END IF;
END$$
DELIMITER ;
CALL astock_add_wave_bottom_columns();
DROP PROCEDURE astock_add_wave_bottom_columns;

CREATE TABLE IF NOT EXISTS wave_bottom_collection_job (
    id BIGINT NOT NULL AUTO_INCREMENT,
    event_id BIGINT NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    focused_at DATETIME(6) NOT NULL,
    data_end_date DATE NOT NULL,
    required_daily_bars SMALLINT UNSIGNED NOT NULL DEFAULT 120,
    adjust_mode VARCHAR(24) NOT NULL DEFAULT 'NONE',
    algorithm_version VARCHAR(32) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'PENDING',
    lease_token CHAR(36) NULL,
    lease_owner VARCHAR(128) NULL,
    lease_expires_at DATETIME(6) NULL,
    attempt_count SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(6) NULL,
    last_error VARCHAR(2000) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    completed_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_wave_bottom_job_event_version (event_id,algorithm_version),
    KEY ix_wave_bottom_job_claim (status,next_attempt_at,lease_expires_at,id),
    KEY ix_wave_bottom_job_symbol (symbol,focused_at,id),
    CONSTRAINT fk_wave_bottom_job_event FOREIGN KEY (event_id)
        REFERENCES pair_trend_live_event(id) ON DELETE CASCADE,
    CONSTRAINT chk_wave_bottom_job_required_bars
        CHECK (required_daily_bars BETWEEN 60 AND 120)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

SET @wave_score_check_exists = (
    SELECT COUNT(*) FROM information_schema.table_constraints
    WHERE constraint_schema=DATABASE()
      AND table_name='pair_trend_live_event'
      AND constraint_name='chk_pair_trend_wave_score'
      AND constraint_type='CHECK'
);
SET @wave_score_check_sql = IF(
    @wave_score_check_exists=0,
    'ALTER TABLE pair_trend_live_event ADD CONSTRAINT chk_pair_trend_wave_score CHECK (wave_score IS NULL OR wave_score BETWEEN 0 AND 100)',
    'DO 0'
);
PREPARE wave_score_check_stmt FROM @wave_score_check_sql;
EXECUTE wave_score_check_stmt;
DEALLOCATE PREPARE wave_score_check_stmt;

SET @wave_index_exists = (
    SELECT COUNT(*) FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event'
      AND index_name='ix_pair_trend_live_wave_signal'
);
SET @wave_index_sql = IF(
    @wave_index_exists=0,
    'ALTER TABLE pair_trend_live_event ADD KEY ix_pair_trend_live_wave_signal (wave_signal,wave_score,root_5m_eob,id)',
    'DO 0'
);
PREPARE wave_index_stmt FROM @wave_index_sql;
EXECUTE wave_index_stmt;
DEALLOCATE PREPARE wave_index_stmt;

-- Existing historical-data events are queued as well; the single collector
-- process drains them in batches of at most 200 symbols.
INSERT INTO wave_bottom_collection_job(
    event_id,symbol,focused_at,data_end_date,required_daily_bars,
    adjust_mode,algorithm_version,status)
SELECT id,symbol,focused_at,DATE_SUB(DATE(focused_at),INTERVAL 1 DAY),120,
       'NONE','pair-wave-bottom-v2','PENDING'
FROM pair_trend_live_event
WHERE algorithm_version='pair-trend-v3' AND pivot_type='BOTTOM'
  AND stage='FOCUS' AND is_active=TRUE AND focused_at IS NOT NULL
ON DUPLICATE KEY UPDATE event_id=VALUES(event_id);

UPDATE pair_trend_live_event event
JOIN wave_bottom_collection_job job ON job.event_id=event.id
SET event.wave_calculation_status=CASE
        WHEN event.wave_calculation_status='COMPLETED' AND
             event.wave_algorithm_version=job.algorithm_version THEN 'COMPLETED'
        ELSE 'PENDING'
    END
WHERE event.algorithm_version='pair-trend-v3' AND event.pivot_type='BOTTOM'
  AND event.stage='FOCUS' AND event.is_active=TRUE
  AND event.focused_at IS NOT NULL;

INSERT INTO schema_migration(version,description)
VALUES ('031','on-demand wave-bottom daily history collection and event signal')
ON DUPLICATE KEY UPDATE description=VALUES(description);
