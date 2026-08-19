USE astock_monitor;

-- Keep wave filtering in the strongly-consistent grouped-query projection.
-- The canonical live table remains the source of truth; the triggers below copy
-- the complete after-image in the same MySQL transaction.
DROP PROCEDURE IF EXISTS astock_add_wave_query_projection;
DELIMITER $$
CREATE PROCEDURE astock_add_wave_query_projection()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_query_event' AND column_name='wave_calculation_status') THEN
        ALTER TABLE pair_trend_query_event ADD COLUMN wave_calculation_status VARCHAR(24) NOT NULL DEFAULT 'NOT_ELIGIBLE' AFTER current_is_active;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_query_event' AND column_name='wave_signal') THEN
        ALTER TABLE pair_trend_query_event ADD COLUMN wave_signal VARCHAR(24) NULL AFTER wave_calculation_status;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_query_event' AND column_name='wave_score') THEN
        ALTER TABLE pair_trend_query_event ADD COLUMN wave_score TINYINT UNSIGNED NULL AFTER wave_signal;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_query_event' AND column_name='wave_algorithm_version') THEN
        ALTER TABLE pair_trend_query_event ADD COLUMN wave_algorithm_version VARCHAR(32) NULL AFTER wave_score;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='pair_trend_query_event' AND column_name='wave_revision') THEN
        ALTER TABLE pair_trend_query_event ADD COLUMN wave_revision INT UNSIGNED NOT NULL DEFAULT 0 AFTER wave_algorithm_version;
    END IF;
END$$
DELIMITER ;
CALL astock_add_wave_query_projection();
DROP PROCEDURE astock_add_wave_query_projection;

UPDATE pair_trend_query_event projection
JOIN pair_trend_live_event source ON source.id=projection.event_id
SET projection.wave_calculation_status=source.wave_calculation_status,
    projection.wave_signal=source.wave_signal,
    projection.wave_score=source.wave_score,
    projection.wave_algorithm_version=source.wave_algorithm_version,
    projection.wave_revision=source.wave_revision
WHERE source.algorithm_version='pair-trend-v3' AND source.root_5m_eob IS NOT NULL;

SET @projection_wave_index_exists = (
    SELECT COUNT(*) FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='pair_trend_query_event'
      AND index_name='ix_pair_trend_query_wave_signal'
);
SET @projection_wave_index_sql = IF(
    @projection_wave_index_exists=0,
    'ALTER TABLE pair_trend_query_event ADD KEY ix_pair_trend_query_wave_signal (algorithm_version,wave_calculation_status,wave_signal,root_5m_eob,symbol,event_id,wave_score)',
    'DO 0'
);
PREPARE projection_wave_index_stmt FROM @projection_wave_index_sql;
EXECUTE projection_wave_index_stmt;
DEALLOCATE PREPARE projection_wave_index_stmt;

SET @live_wave_query_index_exists = (
    SELECT COUNT(*) FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event'
      AND index_name='ix_pair_trend_live_wave_query'
);
SET @live_wave_query_index_sql = IF(
    @live_wave_query_index_exists=0,
    'ALTER TABLE pair_trend_live_event ADD KEY ix_pair_trend_live_wave_query (algorithm_version,wave_calculation_status,wave_signal,root_5m_eob,symbol,id,wave_score)',
    'DO 0'
);
PREPARE live_wave_query_index_stmt FROM @live_wave_query_index_sql;
EXECUTE live_wave_query_index_stmt;
DEALLOCATE PREPARE live_wave_query_index_stmt;

DROP TRIGGER IF EXISTS trg_pair_trend_query_event_insert;
DROP TRIGGER IF EXISTS trg_pair_trend_query_event_update;
DROP TRIGGER IF EXISTS trg_pair_trend_query_event_delete;

DELIMITER $$

CREATE TRIGGER trg_pair_trend_query_event_insert
AFTER INSERT ON pair_trend_live_event
FOR EACH ROW
BEGIN
    IF NEW.algorithm_version='pair-trend-v3' AND NEW.root_5m_eob IS NOT NULL THEN
        INSERT INTO pair_trend_query_event(
            event_id,event_key,algorithm_version,symbol,symbol_name,root_5m_eob,pivot_type,
            frequencies,frequency_mask,observed_at,focused_at,established_at,invalidated_at,
            current_stage,current_is_active,wave_calculation_status,wave_signal,wave_score,
            wave_algorithm_version,wave_revision,source_revision,source_content_hash,source_updated_at)
        VALUES(
            NEW.id,NEW.event_key,NEW.algorithm_version,NEW.symbol,NEW.symbol_name,
            NEW.root_5m_eob,NEW.pivot_type,NEW.frequencies,
            (IF(FIND_IN_SET('5m',NEW.frequencies)>0,1,0) |
             IF(FIND_IN_SET('30m',NEW.frequencies)>0,2,0) |
             IF(FIND_IN_SET('60m',NEW.frequencies)>0,4,0) |
             IF(FIND_IN_SET('1d',NEW.frequencies)>0,8,0)),
            NEW.observed_at,NEW.focused_at,NEW.established_at,NEW.invalidated_at,
            NEW.stage,NEW.is_active,NEW.wave_calculation_status,NEW.wave_signal,
            NEW.wave_score,NEW.wave_algorithm_version,NEW.wave_revision,
            NEW.event_revision,NEW.content_hash,NEW.updated_at);
    END IF;
END$$

CREATE TRIGGER trg_pair_trend_query_event_update
AFTER UPDATE ON pair_trend_live_event
FOR EACH ROW
BEGIN
    IF OLD.algorithm_version='pair-trend-v3' AND OLD.root_5m_eob IS NOT NULL AND
       (NEW.algorithm_version<>'pair-trend-v3' OR NEW.root_5m_eob IS NULL) THEN
        DELETE FROM pair_trend_query_event WHERE event_id=OLD.id;
    END IF;

    IF NEW.algorithm_version='pair-trend-v3' AND NEW.root_5m_eob IS NOT NULL AND
       (OLD.algorithm_version<>'pair-trend-v3' OR OLD.root_5m_eob IS NULL OR
        NOT (NEW.event_key <=> OLD.event_key) OR NOT (NEW.symbol <=> OLD.symbol) OR
        NOT (NEW.symbol_name <=> OLD.symbol_name) OR
        NOT (NEW.root_5m_eob <=> OLD.root_5m_eob) OR
        NOT (NEW.pivot_type <=> OLD.pivot_type) OR
        NOT (NEW.frequencies <=> OLD.frequencies) OR
        NOT (NEW.observed_at <=> OLD.observed_at) OR
        NOT (NEW.focused_at <=> OLD.focused_at) OR
        NOT (NEW.established_at <=> OLD.established_at) OR
        NOT (NEW.invalidated_at <=> OLD.invalidated_at) OR
        NOT (NEW.stage <=> OLD.stage) OR NOT (NEW.is_active <=> OLD.is_active) OR
        NOT (NEW.wave_calculation_status <=> OLD.wave_calculation_status) OR
        NOT (NEW.wave_signal <=> OLD.wave_signal) OR
        NOT (NEW.wave_score <=> OLD.wave_score) OR
        NOT (NEW.wave_algorithm_version <=> OLD.wave_algorithm_version) OR
        NOT (NEW.wave_revision <=> OLD.wave_revision) OR
        NOT (NEW.event_revision <=> OLD.event_revision) OR
        NOT (NEW.content_hash <=> OLD.content_hash)) THEN
        INSERT INTO pair_trend_query_event(
            event_id,event_key,algorithm_version,symbol,symbol_name,root_5m_eob,pivot_type,
            frequencies,frequency_mask,observed_at,focused_at,established_at,invalidated_at,
            current_stage,current_is_active,wave_calculation_status,wave_signal,wave_score,
            wave_algorithm_version,wave_revision,source_revision,source_content_hash,source_updated_at)
        VALUES(
            NEW.id,NEW.event_key,NEW.algorithm_version,NEW.symbol,NEW.symbol_name,
            NEW.root_5m_eob,NEW.pivot_type,NEW.frequencies,
            (IF(FIND_IN_SET('5m',NEW.frequencies)>0,1,0) |
             IF(FIND_IN_SET('30m',NEW.frequencies)>0,2,0) |
             IF(FIND_IN_SET('60m',NEW.frequencies)>0,4,0) |
             IF(FIND_IN_SET('1d',NEW.frequencies)>0,8,0)),
            NEW.observed_at,NEW.focused_at,NEW.established_at,NEW.invalidated_at,
            NEW.stage,NEW.is_active,NEW.wave_calculation_status,NEW.wave_signal,
            NEW.wave_score,NEW.wave_algorithm_version,NEW.wave_revision,
            NEW.event_revision,NEW.content_hash,NEW.updated_at)
        ON DUPLICATE KEY UPDATE
            event_key=VALUES(event_key),algorithm_version=VALUES(algorithm_version),
            symbol=VALUES(symbol),symbol_name=VALUES(symbol_name),root_5m_eob=VALUES(root_5m_eob),
            pivot_type=VALUES(pivot_type),frequencies=VALUES(frequencies),
            frequency_mask=VALUES(frequency_mask),observed_at=VALUES(observed_at),
            focused_at=VALUES(focused_at),established_at=VALUES(established_at),
            invalidated_at=VALUES(invalidated_at),current_stage=VALUES(current_stage),
            current_is_active=VALUES(current_is_active),
            wave_calculation_status=VALUES(wave_calculation_status),
            wave_signal=VALUES(wave_signal),wave_score=VALUES(wave_score),
            wave_algorithm_version=VALUES(wave_algorithm_version),
            wave_revision=VALUES(wave_revision),source_revision=VALUES(source_revision),
            source_content_hash=VALUES(source_content_hash),source_updated_at=VALUES(source_updated_at);
    END IF;
END$$

CREATE TRIGGER trg_pair_trend_query_event_delete
AFTER DELETE ON pair_trend_live_event
FOR EACH ROW
BEGIN
    DELETE FROM pair_trend_query_event WHERE event_id=OLD.id;
END$$

DELIMITER ;

INSERT INTO schema_migration(version,description)
VALUES ('034','wave signal filters and score sorting in pair trend query projection')
ON DUPLICATE KEY UPDATE description=VALUES(description);
