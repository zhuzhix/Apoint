-- ============================================================================
-- 清理对子顶底旧版本 pair-trend-v2 历史回测数据
--
-- 清理对象：
--   pair_trend_backtest_run.algorithm_version = 'pair-trend-v2'
--   以及这些任务关联的 symbol / hit / lifecycle / event 数据。
--
-- 不会清理：
--   1. pair-trend-v3 回测结果；
--   2. pair_trend_live_event / pair_trend_live_hit 等盘中实时表；
--   3. K 线行情表。
--
-- 安全说明：
--   1. 文件末尾默认使用预览模式（第二个参数为 0），不会删除数据。
--   2. 确认预览结果后，将第二个参数改为 1 才会正式执行。
--   3. 正式清理按任务处理，并将大表按 50000 行分批提交，支持中断后重跑。
--   4. 请在回测任务停止后执行，避免清理过程中旧版本任务继续写入。
-- ============================================================================

USE astock_monitor;

DROP PROCEDURE IF EXISTS purge_pair_trend_old_version;

DELIMITER $$

CREATE PROCEDURE purge_pair_trend_old_version(
    IN p_algorithm_version VARCHAR(32),
    IN p_execute TINYINT
)
BEGIN
    DECLARE v_run_id BIGINT DEFAULT NULL;
    DECLARE v_rows INT DEFAULT 0;
    DECLARE v_run_count INT DEFAULT 0;
    DECLARE v_processed_runs INT DEFAULT 0;

    IF DATABASE() <> 'astock_monitor' THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = '安全校验失败：当前数据库不是 astock_monitor';
    END IF;

    -- 本脚本只允许删除已经废弃的 V2，防止参数误写后清除 V3。
    IF p_algorithm_version <> 'pair-trend-v2' THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = '安全校验失败：本脚本只允许清理 pair-trend-v2';
    END IF;

    SELECT COUNT(*)
    INTO v_run_count
    FROM pair_trend_backtest_run
    WHERE algorithm_version = p_algorithm_version;

    -- 删除前任务清单。hits_detected/events_written 是任务运行时保存的统计值。
    SELECT
        id,
        run_key,
        algorithm_version,
        status,
        date_from,
        date_to,
        requested_symbols,
        completed_symbols,
        failed_symbols,
        hits_detected,
        events_written,
        started_at,
        finished_at
    FROM pair_trend_backtest_run
    WHERE algorithm_version = p_algorithm_version
    ORDER BY id;

    SELECT
        p_algorithm_version AS target_algorithm_version,
        v_run_count AS run_count,
        COALESCE(SUM(hits_detected), 0) AS recorded_hits,
        COALESCE(SUM(events_written), 0) AS recorded_events,
        CASE WHEN p_execute = 1 THEN 'EXECUTE' ELSE 'PREVIEW_ONLY' END AS run_mode
    FROM pair_trend_backtest_run
    WHERE algorithm_version = p_algorithm_version;

    IF p_execute <> 1 THEN
        SELECT
            '当前为预览模式，未删除数据。确认后将 CALL 的第二个参数改为 1。'
                AS result;
    ELSE
        purge_runs: LOOP
            SET v_run_id = NULL;

            SELECT MIN(id)
            INTO v_run_id
            FROM pair_trend_backtest_run
            WHERE algorithm_version = p_algorithm_version;

            IF v_run_id IS NULL THEN
                LEAVE purge_runs;
            END IF;

            -- 先删除最宽、数量最大的命中明细表。
            purge_hits: LOOP
                DELETE FROM pair_trend_hit
                WHERE run_id = v_run_id
                LIMIT 50000;

                SET v_rows = ROW_COUNT();
                COMMIT;

                IF v_rows = 0 THEN
                    LEAVE purge_hits;
                END IF;
            END LOOP;

            -- 删除状态变化审计记录，解除其对 event 的外键引用。
            purge_lifecycle: LOOP
                DELETE FROM pair_trend_lifecycle
                WHERE run_id = v_run_id
                LIMIT 50000;

                SET v_rows = ROW_COUNT();
                COMMIT;

                IF v_rows = 0 THEN
                    LEAVE purge_lifecycle;
                END IF;
            END LOOP;

            -- hit 和 lifecycle 清空后，再分批删除事件汇总。
            purge_events: LOOP
                DELETE FROM pair_trend_event
                WHERE run_id = v_run_id
                LIMIT 50000;

                SET v_rows = ROW_COUNT();
                COMMIT;

                IF v_rows = 0 THEN
                    LEAVE purge_events;
                END IF;
            END LOOP;

            DELETE FROM pair_trend_backtest_symbol
            WHERE run_id = v_run_id;
            COMMIT;

            -- 子表已经清空，最后删除回测任务本身。
            DELETE FROM pair_trend_backtest_run
            WHERE id = v_run_id
              AND algorithm_version = p_algorithm_version;
            COMMIT;

            SET v_processed_runs = v_processed_runs + 1;

            SELECT
                'run_cleaned' AS progress,
                v_run_id AS deleted_run_id,
                v_processed_runs AS processed_runs,
                v_run_count AS total_runs;
        END LOOP;

        -- 同步运维页面使用的数据量快照。使用 InnoDB 估算值，避免清理后再次
        -- 对仍然很大的 V3 表执行全表 COUNT(*)。
        INSERT INTO dataset_stat_snapshot
            (dataset_name, row_count, is_exact, updated_at)
        SELECT
            table_name,
            COALESCE(table_rows, 0),
            FALSE,
            UTC_TIMESTAMP(6)
        FROM information_schema.tables
        WHERE table_schema = DATABASE()
          AND table_name IN ('pair_trend_event', 'pair_trend_hit')
        ON DUPLICATE KEY UPDATE
            row_count = VALUES(row_count),
            is_exact = VALUES(is_exact),
            updated_at = VALUES(updated_at);

        SELECT
            '清理完成' AS result,
            p_algorithm_version AS deleted_algorithm_version,
            v_processed_runs AS deleted_run_count;

        SELECT
            table_name,
            table_rows AS estimated_remaining_rows,
            ROUND((data_length + index_length) / 1024 / 1024 / 1024, 2)
                AS allocated_size_gb
        FROM information_schema.tables
        WHERE table_schema = DATABASE()
          AND table_name IN ('pair_trend_event', 'pair_trend_hit')
        ORDER BY table_name;
    END IF;
END$$

DELIMITER ;

-- 第一次运行：只预览所有 pair-trend-v2 回测任务，不删除数据。
CALL purge_pair_trend_old_version('pair-trend-v2', 0);

-- 正式执行：确认预览无误后，注释上一行并取消下一行注释。
-- CALL purge_pair_trend_old_version('pair-trend-v2', 1);

DROP PROCEDURE IF EXISTS purge_pair_trend_old_version;

-- 注意：DELETE 后空间会先归还给 InnoDB 供后续数据复用，数据文件不一定立即缩小。
-- 若必须立即归还磁盘空间，需要另行评估 OPTIMIZE TABLE；该操作会重建大表，
-- 需要额外临时磁盘空间，并可能在部分阶段阻塞业务，本脚本不会自动执行。
