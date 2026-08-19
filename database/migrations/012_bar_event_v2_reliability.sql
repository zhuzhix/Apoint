USE astock_monitor;

-- V2 Outbox 多实例租约。迁移可重复执行；正式执行需避开历史回填写入高峰。
DROP PROCEDURE IF EXISTS add_bar_outbox_v2_column;
DELIMITER //
CREATE PROCEDURE add_bar_outbox_v2_column(
    IN column_name_value VARCHAR(64),
    IN column_definition_value VARCHAR(512)
)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema=DATABASE()
          AND table_name='bar_event_outbox'
          AND column_name=column_name_value
    ) THEN
        SET @ddl=CONCAT(
            'ALTER TABLE bar_event_outbox ADD COLUMN `',
            column_name_value, '` ', column_definition_value
        );
        PREPARE statement_to_run FROM @ddl;
        EXECUTE statement_to_run;
        DEALLOCATE PREPARE statement_to_run;
    END IF;
END //
DELIMITER ;

CALL add_bar_outbox_v2_column('lease_owner',
    'VARCHAR(128) NULL AFTER status');
CALL add_bar_outbox_v2_column('lease_expires_at',
    'DATETIME(6) NULL AFTER lease_owner');
CALL add_bar_outbox_v2_column('stream_id',
    'VARCHAR(64) NULL AFTER lease_expires_at');

DROP PROCEDURE IF EXISTS add_bar_outbox_v2_column;

DROP PROCEDURE IF EXISTS add_bar_outbox_v2_index;
DELIMITER //
CREATE PROCEDURE add_bar_outbox_v2_index()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema=DATABASE()
          AND table_name='bar_event_outbox'
          AND index_name='ix_bar_event_outbox_lease'
    ) THEN
        ALTER TABLE bar_event_outbox
            ADD KEY ix_bar_event_outbox_lease
                (status, lease_expires_at, next_attempt_at, id);
    END IF;
END //
DELIMITER ;

CALL add_bar_outbox_v2_index();
DROP PROCEDURE IF EXISTS add_bar_outbox_v2_index;

-- 上一实例异常退出后，过期租约由发布器自动接管；迁移时只恢复已经过期的记录。
UPDATE bar_event_outbox
SET status='pending', lease_owner=NULL, lease_expires_at=NULL
WHERE status='publishing'
  AND (lease_expires_at IS NULL OR lease_expires_at<CURRENT_TIMESTAMP(6));

INSERT INTO schema_migration (version, description)
VALUES ('012', 'V2 bar lifecycle contract and leased reliable outbox publishing')
ON DUPLICATE KEY UPDATE description=VALUES(description);
