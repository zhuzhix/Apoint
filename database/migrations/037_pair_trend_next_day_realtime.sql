USE astock_monitor;

-- 盘中验证在首根闭合5分钟K线至收盘之间保持 MONITORING；只有突破、
-- 收盘通过或全天无成交后才进入终态。该状态仍属于未完成任务。
DROP PROCEDURE IF EXISTS astock_extend_next_day_validation_status;
DELIMITER $$
CREATE PROCEDURE astock_extend_next_day_validation_status()
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_schema=DATABASE()
          AND table_name='pair_trend_next_day_validation'
          AND constraint_name='chk_pair_trend_next_day_status'
          AND constraint_type='CHECK'
    ) THEN
        ALTER TABLE pair_trend_next_day_validation
            DROP CHECK chk_pair_trend_next_day_status;
    END IF;
    ALTER TABLE pair_trend_next_day_validation
        ADD CONSTRAINT chk_pair_trend_next_day_status
        CHECK (status IN (
            'PENDING','MONITORING','RETRY','LEASED','PASSED','INVALIDATED',
            'NO_TRADE','NOT_APPLICABLE','FAILED'));
END$$
DELIMITER ;
CALL astock_extend_next_day_validation_status();
DROP PROCEDURE astock_extend_next_day_validation_status;

INSERT INTO schema_migration(version,description)
VALUES ('037','realtime next-trading-day validation monitoring state')
ON DUPLICATE KEY UPDATE description=VALUES(description);
