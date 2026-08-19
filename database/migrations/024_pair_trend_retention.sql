USE astock_monitor;

CREATE TABLE IF NOT EXISTS pair_trend_retention_run (
    id BIGINT NOT NULL AUTO_INCREMENT,
    cutoff_date DATE NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'running',
    planned_event_count BIGINT NOT NULL DEFAULT 0,
    planned_hit_count BIGINT NOT NULL DEFAULT 0,
    planned_lifecycle_count BIGINT NOT NULL DEFAULT 0,
    deleted_event_count BIGINT NOT NULL DEFAULT 0,
    last_event_id BIGINT NOT NULL DEFAULT 0,
    max_event_id BIGINT NOT NULL DEFAULT 0,
    started_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    finished_at DATETIME(6) NULL,
    error_message VARCHAR(2000) NULL,
    PRIMARY KEY (id),
    KEY ix_pair_trend_retention_status (status, started_at),
    KEY ix_pair_trend_retention_cutoff (cutoff_date, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pair_trend_backtest_run_archive (
    id BIGINT NOT NULL AUTO_INCREMENT,
    source_run_id BIGINT NOT NULL,
    retention_run_id BIGINT NOT NULL,
    archive_cutoff DATE NOT NULL,
    run_key VARCHAR(200) NOT NULL,
    algorithm_version VARCHAR(32) NOT NULL,
    run_mode VARCHAR(24) NOT NULL,
    status VARCHAR(24) NOT NULL,
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    source_snapshot_json JSON NOT NULL,
    archived_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_pair_trend_run_archive (source_run_id, archive_cutoff),
    KEY ix_pair_trend_run_archive_retention (retention_run_id),
    KEY ix_pair_trend_run_archive_dates (date_from, date_to)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS run_pair_trend_retention;
DROP PROCEDURE IF EXISTS run_pair_trend_retention_monthly;

DELIMITER $$

CREATE PROCEDURE run_pair_trend_retention(IN p_cutoff_date DATE)
BEGIN
    DECLARE v_retention_run_id BIGINT DEFAULT 0;
    DECLARE v_last_id BIGINT DEFAULT 0;
    DECLARE v_upper_id BIGINT DEFAULT 0;
    DECLARE v_max_id BIGINT DEFAULT 0;
    DECLARE v_rows BIGINT DEFAULT 0;
    DECLARE v_deleted_events BIGINT DEFAULT 0;
    DECLARE v_planned_events BIGINT DEFAULT 0;
    DECLARE v_planned_hits BIGINT DEFAULT 0;
    DECLARE v_planned_lifecycle BIGINT DEFAULT 0;
    DECLARE v_message TEXT DEFAULT NULL;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        GET DIAGNOSTICS CONDITION 1 v_message = MESSAGE_TEXT;
        IF v_retention_run_id > 0 THEN
            UPDATE pair_trend_retention_run
            SET status = 'failed',
                error_message = LEFT(v_message, 2000),
                finished_at = UTC_TIMESTAMP(6)
            WHERE id = v_retention_run_id;
            COMMIT;
        END IF;
        RESIGNAL;
    END;

    IF DATABASE() <> 'astock_monitor' THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Safety check failed: current database is not astock_monitor';
    END IF;

    IF p_cutoff_date IS NULL
       OR p_cutoff_date < DATE('2026-01-01')
       OR p_cutoff_date > CURRENT_DATE() THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Safety check failed: invalid retention cutoff date';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pair_trend_retention_run
        WHERE status = 'running'
    ) THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'A pair trend retention run is already active';
    END IF;

    SELECT COUNT(*)
    INTO v_planned_events
    FROM pair_trend_event
    WHERE last_seen_at < p_cutoff_date;

    SELECT COUNT(*)
    INTO v_planned_hits
    FROM pair_trend_hit h
    INNER JOIN pair_trend_event e ON e.id = h.event_id
    WHERE e.last_seen_at < p_cutoff_date;

    SELECT COUNT(*)
    INTO v_planned_lifecycle
    FROM pair_trend_lifecycle l
    INNER JOIN pair_trend_event e ON e.id = l.event_id
    WHERE e.last_seen_at < p_cutoff_date;

    SELECT COALESCE(MAX(id), 0)
    INTO v_max_id
    FROM pair_trend_event
    WHERE last_seen_at < p_cutoff_date;

    INSERT INTO pair_trend_retention_run
        (cutoff_date, status, planned_event_count, planned_hit_count,
         planned_lifecycle_count, max_event_id)
    VALUES
        (p_cutoff_date, 'running', v_planned_events, v_planned_hits,
         v_planned_lifecycle, v_max_id);

    SET v_retention_run_id = LAST_INSERT_ID();
    COMMIT;

    INSERT INTO pair_trend_backtest_run_archive
        (source_run_id, retention_run_id, archive_cutoff, run_key,
         algorithm_version, run_mode, status, date_from, date_to,
         source_snapshot_json)
    SELECT
        id,
        v_retention_run_id,
        p_cutoff_date,
        run_key,
        algorithm_version,
        run_mode,
        status,
        date_from,
        date_to,
        JSON_OBJECT(
            'data_source', data_source,
            'notes', notes,
            'frequencies', frequencies,
            'parameters', parameters_json,
            'requested_symbols', requested_symbols,
            'completed_symbols', completed_symbols,
            'failed_symbols', failed_symbols,
            'bars_processed', bars_processed,
            'hits_detected', hits_detected,
            'events_written', events_written,
            'started_at', started_at,
            'finished_at', finished_at
        )
    FROM pair_trend_backtest_run
    WHERE date_from < p_cutoff_date
    ON DUPLICATE KEY UPDATE
        retention_run_id = VALUES(retention_run_id),
        source_snapshot_json = VALUES(source_snapshot_json),
        archived_at = UTC_TIMESTAMP(6);
    COMMIT;

    WHILE v_last_id < v_max_id DO
        SET v_upper_id = LEAST(v_last_id + 10000, v_max_id);

        -- 先直接删除子表，避免父表 ON DELETE CASCADE 在单条语句中持有
        -- 数万条子记录和索引锁。每个阶段独立提交，适配低内存云主机，
        -- 中断后重跑也具备幂等性。
        DELETE l
        FROM pair_trend_event e
        STRAIGHT_JOIN pair_trend_lifecycle l ON l.event_id = e.id
        WHERE e.id > v_last_id
          AND e.id <= v_upper_id
          AND e.last_seen_at < p_cutoff_date;
        COMMIT;

        DELETE h
        FROM pair_trend_event e
        STRAIGHT_JOIN pair_trend_hit h ON h.event_id = e.id
        WHERE e.id > v_last_id
          AND e.id <= v_upper_id
          AND e.last_seen_at < p_cutoff_date;
        COMMIT;

        DELETE FROM pair_trend_event
        WHERE id > v_last_id
          AND id <= v_upper_id
          AND last_seen_at < p_cutoff_date;

        SET v_rows = ROW_COUNT();
        SET v_deleted_events = v_deleted_events + v_rows;
        SET v_last_id = v_upper_id;

        UPDATE pair_trend_retention_run
        SET deleted_event_count = v_deleted_events,
            last_event_id = v_last_id
        WHERE id = v_retention_run_id;
        COMMIT;
    END WHILE;

    DELETE FROM pair_trend_backtest_run
    WHERE status = 'complete'
      AND date_to < p_cutoff_date;
    COMMIT;

    UPDATE pair_trend_retention_run
    SET status = 'complete',
        deleted_event_count = v_deleted_events,
        last_event_id = v_max_id,
        finished_at = UTC_TIMESTAMP(6)
    WHERE id = v_retention_run_id;
    COMMIT;

    INSERT INTO dataset_stat_snapshot
        (dataset_name, row_count, is_exact, updated_at)
    SELECT table_name, COALESCE(table_rows, 0), FALSE, UTC_TIMESTAMP(6)
    FROM information_schema.tables
    WHERE table_schema = DATABASE()
      AND table_name IN (
          'pair_trend_event',
          'pair_trend_hit',
          'pair_trend_lifecycle'
      )
    ON DUPLICATE KEY UPDATE
        row_count = VALUES(row_count),
        is_exact = VALUES(is_exact),
        updated_at = VALUES(updated_at);

    SELECT *
    FROM pair_trend_retention_run
    WHERE id = v_retention_run_id;
END$$

CREATE PROCEDURE run_pair_trend_retention_monthly()
BEGIN
    DECLARE v_cutoff_date DATE;
    SET v_cutoff_date = STR_TO_DATE(
        DATE_FORMAT(CURRENT_DATE - INTERVAL 1 MONTH, '%Y-%m-01'),
        '%Y-%m-%d'
    );
    CALL run_pair_trend_retention(v_cutoff_date);
END$$

DELIMITER ;

DROP EVENT IF EXISTS ev_pair_trend_retention_monthly;
CREATE EVENT ev_pair_trend_retention_monthly
    ON SCHEDULE EVERY 1 MONTH
    STARTS '2026-09-01 03:30:00'
    ON COMPLETION PRESERVE
    ENABLE
    DO CALL run_pair_trend_retention_monthly();

INSERT INTO schema_migration (version, description)
VALUES ('024', 'pair trend monthly retention and backtest run archive metadata')
ON DUPLICATE KEY UPDATE description = VALUES(description);
