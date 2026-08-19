-- ============================================================================
-- 清理 kline_bar_5m 中 2026 年 8 月以前的数据
--
-- 保留范围 : trading_date >= '2026-08-01'
-- 清理范围 : trading_date <  '2026-08-01'
-- 清理方式 : 删除截止日期以前的完整月分区，避免对大表逐行 DELETE。
--
-- 安全说明：
--   1. 脚本默认只预览，不删除数据。
--   2. 确认预览结果后，将文件末尾 CALL 的第二个参数从 0 改成 1。
--   3. DROP PARTITION 属于 DDL，会自动提交，不能通过 ROLLBACK 恢复。
--   4. 建议停止写入 kline_bar_5m 的采集/补数任务后再执行正式清理。
-- ============================================================================

USE astock_monitor;

DROP PROCEDURE IF EXISTS purge_kline_bar_5m_before;

DELIMITER $$

CREATE PROCEDURE purge_kline_bar_5m_before(
    IN p_cutoff_date DATE,
    IN p_execute TINYINT
)
BEGIN
    DECLARE v_partition_names TEXT DEFAULT NULL;
    DECLARE v_partition_count INT DEFAULT 0;
    DECLARE v_estimated_rows BIGINT DEFAULT 0;

    IF DATABASE() <> 'astock_monitor' THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = '安全校验失败：当前数据库不是 astock_monitor';
    END IF;

    IF p_cutoff_date <> DATE('2026-08-01') THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = '安全校验失败：本脚本只允许使用截止日期 2026-08-01';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = DATABASE()
          AND table_name = 'kline_bar_5m'
    ) THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = '目标表 astock_monitor.kline_bar_5m 不存在';
    END IF;

    -- RANGE COLUMNS 分区的 PARTITION_DESCRIPTION 是该分区的上界。
    -- 仅选择上界小于或等于截止日的完整分区，确保不会删除 8 月数据。
    SELECT
        GROUP_CONCAT(
            CONCAT('`', partition_name, '`')
            ORDER BY partition_ordinal_position
            SEPARATOR ','
        ),
        COUNT(*),
        COALESCE(SUM(table_rows), 0)
    INTO
        v_partition_names,
        v_partition_count,
        v_estimated_rows
    FROM information_schema.partitions
    WHERE table_schema = DATABASE()
      AND table_name = 'kline_bar_5m'
      AND partition_name IS NOT NULL
      AND partition_description <> 'MAXVALUE'
      AND STR_TO_DATE(
            REPLACE(partition_description, CHAR(39), ''),
            '%Y-%m-%d'
          ) <= p_cutoff_date;

    -- 删除前预览。table_rows 是 InnoDB 统计值，只用于估算。
    SELECT
        DATABASE() AS target_database,
        'kline_bar_5m' AS target_table,
        p_cutoff_date AS cutoff_date,
        'trading_date < cutoff_date' AS delete_condition,
        v_partition_names AS partitions_to_drop,
        v_partition_count AS partition_count,
        v_estimated_rows AS estimated_rows,
        CASE WHEN p_execute = 1 THEN 'EXECUTE' ELSE 'PREVIEW_ONLY' END AS run_mode;

    IF p_execute = 1 THEN
        IF v_partition_count = 0 OR v_partition_names IS NULL THEN
            SELECT '没有符合条件的历史分区，无需清理。' AS result;
        ELSE
            SET @drop_partition_sql = CONCAT(
                'ALTER TABLE `astock_monitor`.`kline_bar_5m` DROP PARTITION ',
                v_partition_names
            );

            PREPARE drop_partition_statement FROM @drop_partition_sql;
            EXECUTE drop_partition_statement;
            DEALLOCATE PREPARE drop_partition_statement;

            -- 同步运维页面使用的数据量快照；分区统计为估算值。
            INSERT INTO dataset_stat_snapshot
                (dataset_name, row_count, is_exact, updated_at)
            SELECT
                'kline_bar_5m',
                COALESCE(SUM(table_rows), 0),
                FALSE,
                UTC_TIMESTAMP(6)
            FROM information_schema.partitions
            WHERE table_schema = DATABASE()
              AND table_name = 'kline_bar_5m'
            ON DUPLICATE KEY UPDATE
                row_count = VALUES(row_count),
                is_exact = VALUES(is_exact),
                updated_at = VALUES(updated_at);

            SELECT
                '清理完成' AS result,
                p_cutoff_date AS retained_from,
                v_partition_names AS dropped_partitions;
        END IF;
    ELSE
        SELECT
            '当前为预览模式，未删除数据。确认后将 CALL 的第二个参数改为 1。'
                AS result;
    END IF;
END$$

DELIMITER ;

-- 第一次运行：仅预览待删除分区，不删除任何 K 线。
CALL purge_kline_bar_5m_before('2026-08-01', 0);

-- 正式执行：确认预览无误后，注释上一行并取消下一行注释。
-- CALL purge_kline_bar_5m_before('2026-08-01', 1);

DROP PROCEDURE IF EXISTS purge_kline_bar_5m_before;
