USE astock_monitor;

-- Narrow, strongly consistent read projection for grouped pair-trend queries.
-- The canonical event table remains the source of truth and detail source.
CREATE TABLE IF NOT EXISTS pair_trend_query_event (
    event_id BIGINT NOT NULL,
    event_key CHAR(64) NOT NULL,
    algorithm_version VARCHAR(32) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    symbol_name VARCHAR(128) NULL,
    root_5m_eob DATETIME(6) NOT NULL,
    pivot_type VARCHAR(16) NOT NULL,
    frequencies VARCHAR(64) NOT NULL,
    frequency_mask TINYINT UNSIGNED NOT NULL,
    observed_at DATETIME(6) NULL,
    focused_at DATETIME(6) NULL,
    established_at DATETIME(6) NULL,
    invalidated_at DATETIME(6) NULL,
    current_stage VARCHAR(24) NOT NULL,
    current_is_active BOOLEAN NOT NULL,
    source_revision INT UNSIGNED NOT NULL,
    source_content_hash CHAR(64) NOT NULL,
    source_updated_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (event_id),
    UNIQUE KEY uk_pair_trend_query_event_key (event_key),
    KEY ix_pair_trend_query_period (
        algorithm_version,root_5m_eob,symbol,event_id,pivot_type,frequency_mask,
        invalidated_at,established_at,focused_at,observed_at),
    KEY ix_pair_trend_query_symbol_period (
        algorithm_version,symbol,root_5m_eob,event_id),
    CONSTRAINT chk_pair_trend_query_frequency_mask CHECK (frequency_mask BETWEEN 1 AND 15)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO pair_trend_query_event(
    event_id,event_key,algorithm_version,symbol,symbol_name,root_5m_eob,pivot_type,
    frequencies,frequency_mask,observed_at,focused_at,established_at,invalidated_at,
    current_stage,current_is_active,source_revision,source_content_hash,source_updated_at)
SELECT id,event_key,algorithm_version,symbol,symbol_name,root_5m_eob,pivot_type,
       frequencies,
       (IF(FIND_IN_SET('5m',frequencies)>0,1,0) |
        IF(FIND_IN_SET('30m',frequencies)>0,2,0) |
        IF(FIND_IN_SET('60m',frequencies)>0,4,0) |
        IF(FIND_IN_SET('1d',frequencies)>0,8,0)),
       observed_at,focused_at,established_at,invalidated_at,stage,is_active,
       event_revision,content_hash,updated_at
FROM pair_trend_live_event
WHERE algorithm_version='pair-trend-v3' AND root_5m_eob IS NOT NULL
ON DUPLICATE KEY UPDATE
    event_key=VALUES(event_key),algorithm_version=VALUES(algorithm_version),
    symbol=VALUES(symbol),symbol_name=VALUES(symbol_name),root_5m_eob=VALUES(root_5m_eob),
    pivot_type=VALUES(pivot_type),frequencies=VALUES(frequencies),
    frequency_mask=VALUES(frequency_mask),observed_at=VALUES(observed_at),
    focused_at=VALUES(focused_at),established_at=VALUES(established_at),
    invalidated_at=VALUES(invalidated_at),current_stage=VALUES(current_stage),
    current_is_active=VALUES(current_is_active),source_revision=VALUES(source_revision),
    source_content_hash=VALUES(source_content_hash),source_updated_at=VALUES(source_updated_at);

DELETE projection
FROM pair_trend_query_event projection
LEFT JOIN pair_trend_live_event source ON source.id=projection.event_id
WHERE source.id IS NULL OR source.algorithm_version<>'pair-trend-v3'
   OR source.root_5m_eob IS NULL;

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
            current_stage,current_is_active,source_revision,source_content_hash,source_updated_at)
        VALUES(
            NEW.id,NEW.event_key,NEW.algorithm_version,NEW.symbol,NEW.symbol_name,
            NEW.root_5m_eob,NEW.pivot_type,NEW.frequencies,
            (IF(FIND_IN_SET('5m',NEW.frequencies)>0,1,0) |
             IF(FIND_IN_SET('30m',NEW.frequencies)>0,2,0) |
             IF(FIND_IN_SET('60m',NEW.frequencies)>0,4,0) |
             IF(FIND_IN_SET('1d',NEW.frequencies)>0,8,0)),
            NEW.observed_at,NEW.focused_at,NEW.established_at,NEW.invalidated_at,
            NEW.stage,NEW.is_active,NEW.event_revision,NEW.content_hash,NEW.updated_at);
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
        NOT (NEW.event_revision <=> OLD.event_revision) OR
        NOT (NEW.content_hash <=> OLD.content_hash)) THEN
        INSERT INTO pair_trend_query_event(
            event_id,event_key,algorithm_version,symbol,symbol_name,root_5m_eob,pivot_type,
            frequencies,frequency_mask,observed_at,focused_at,established_at,invalidated_at,
            current_stage,current_is_active,source_revision,source_content_hash,source_updated_at)
        VALUES(
            NEW.id,NEW.event_key,NEW.algorithm_version,NEW.symbol,NEW.symbol_name,
            NEW.root_5m_eob,NEW.pivot_type,NEW.frequencies,
            (IF(FIND_IN_SET('5m',NEW.frequencies)>0,1,0) |
             IF(FIND_IN_SET('30m',NEW.frequencies)>0,2,0) |
             IF(FIND_IN_SET('60m',NEW.frequencies)>0,4,0) |
             IF(FIND_IN_SET('1d',NEW.frequencies)>0,8,0)),
            NEW.observed_at,NEW.focused_at,NEW.established_at,NEW.invalidated_at,
            NEW.stage,NEW.is_active,NEW.event_revision,NEW.content_hash,NEW.updated_at)
        ON DUPLICATE KEY UPDATE
            event_key=VALUES(event_key),algorithm_version=VALUES(algorithm_version),
            symbol=VALUES(symbol),symbol_name=VALUES(symbol_name),root_5m_eob=VALUES(root_5m_eob),
            pivot_type=VALUES(pivot_type),frequencies=VALUES(frequencies),
            frequency_mask=VALUES(frequency_mask),observed_at=VALUES(observed_at),
            focused_at=VALUES(focused_at),established_at=VALUES(established_at),
            invalidated_at=VALUES(invalidated_at),current_stage=VALUES(current_stage),
            current_is_active=VALUES(current_is_active),source_revision=VALUES(source_revision),
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
VALUES ('030','strongly consistent narrow pair trend grouped-query projection')
ON DUPLICATE KEY UPDATE description=VALUES(description);
