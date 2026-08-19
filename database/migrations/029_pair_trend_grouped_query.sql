USE astock_monitor;

-- 正式 V3 查询只承认 root_5m_eob 作为顶底形成时间。若已有 V3 脏数据，
-- 添加约束会直接失败，禁止用 first_seen_at/last_seen_at 兜底掩盖问题。
SET @pair_trend_v3_root_check_sql = (
    SELECT IF(COUNT(*)=0,
        'ALTER TABLE pair_trend_live_event ADD CONSTRAINT chk_pair_trend_live_v3_root CHECK (algorithm_version<>''pair-trend-v3'' OR root_5m_eob IS NOT NULL)',
        'SELECT 1')
    FROM information_schema.table_constraints
    WHERE constraint_schema=DATABASE() AND table_name='pair_trend_live_event'
      AND constraint_name='chk_pair_trend_live_v3_root'
);
PREPARE pair_trend_v3_root_check_stmt FROM @pair_trend_v3_root_check_sql;
EXECUTE pair_trend_v3_root_check_stmt;
DEALLOCATE PREPARE pair_trend_v3_root_check_stmt;

-- 日期范围先定位正式算法和 root，再完成股票分组。
SET @pair_trend_period_index_sql = (
    SELECT IF(COUNT(*)=0,
        'ALTER TABLE pair_trend_live_event ADD KEY ix_pair_trend_live_period (algorithm_version,root_5m_eob,symbol,id)',
        'SELECT 1')
    FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event'
      AND index_name='ix_pair_trend_live_period'
);
PREPARE pair_trend_period_index_stmt FROM @pair_trend_period_index_sql;
EXECUTE pair_trend_period_index_stmt;
DEALLOCATE PREPARE pair_trend_period_index_stmt;

-- 股票展开查询按 symbol + root 倒序分页；id 是稳定的同时间 tie-breaker。
SET @pair_trend_symbol_period_index_sql = (
    SELECT IF(COUNT(*)=0,
        'ALTER TABLE pair_trend_live_event ADD KEY ix_pair_trend_live_symbol_period (algorithm_version,symbol,root_5m_eob,id)',
        'SELECT 1')
    FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event'
      AND index_name='ix_pair_trend_live_symbol_period'
);
PREPARE pair_trend_symbol_period_index_stmt FROM @pair_trend_symbol_period_index_sql;
EXECUTE pair_trend_symbol_period_index_stmt;
DEALLOCATE PREPARE pair_trend_symbol_period_index_stmt;

-- 平铺事件查询按顶底日期和 ID 稳定倒序，不让 symbol 插在排序列之间。
SET @pair_trend_timeline_index_sql = (
    SELECT IF(COUNT(*)=0,
        'ALTER TABLE pair_trend_live_event ADD KEY ix_pair_trend_live_timeline (algorithm_version,root_5m_eob,id)',
        'SELECT 1')
    FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event'
      AND index_name='ix_pair_trend_live_timeline'
);
PREPARE pair_trend_timeline_index_stmt FROM @pair_trend_timeline_index_sql;
EXECUTE pair_trend_timeline_index_stmt;
DEALLOCATE PREPARE pair_trend_timeline_index_stmt;

DROP PROCEDURE IF EXISTS validate_pair_trend_grouped_query_schema;

DELIMITER $$

CREATE PROCEDURE validate_pair_trend_grouped_query_schema()
BEGIN
    DECLARE v_check_count INT DEFAULT 0;
    DECLARE v_columns TEXT DEFAULT NULL;

    SELECT COUNT(*) INTO v_check_count
    FROM information_schema.check_constraints
    WHERE constraint_schema=DATABASE()
      AND constraint_name='chk_pair_trend_live_v3_root'
      AND LOWER(check_clause) LIKE '%algorithm_version%'
      AND LOWER(check_clause) LIKE '%pair-trend-v3%'
      AND LOWER(check_clause) LIKE '%root_5m_eob%is not null%';
    IF v_check_count <> 1 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT='chk_pair_trend_live_v3_root definition mismatch';
    END IF;

    SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') INTO v_columns
    FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event'
      AND index_name='ix_pair_trend_live_period';
    IF v_columns <> 'algorithm_version,root_5m_eob,symbol,id' THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT='ix_pair_trend_live_period definition mismatch';
    END IF;

    SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') INTO v_columns
    FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event'
      AND index_name='ix_pair_trend_live_symbol_period';
    IF v_columns <> 'algorithm_version,symbol,root_5m_eob,id' THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT='ix_pair_trend_live_symbol_period definition mismatch';
    END IF;

    SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') INTO v_columns
    FROM information_schema.statistics
    WHERE table_schema=DATABASE() AND table_name='pair_trend_live_event'
      AND index_name='ix_pair_trend_live_timeline';
    IF v_columns <> 'algorithm_version,root_5m_eob,id' THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT='ix_pair_trend_live_timeline definition mismatch';
    END IF;
END$$

DELIMITER ;

CALL validate_pair_trend_grouped_query_schema();
DROP PROCEDURE validate_pair_trend_grouped_query_schema;

INSERT INTO schema_migration(version,description)
VALUES ('029','strict V3 root time and grouped pair trend query indexes')
ON DUPLICATE KEY UPDATE description=VALUES(description);
