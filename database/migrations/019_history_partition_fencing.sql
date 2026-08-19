USE astock_monitor;

-- Every partition attempt records the scheduler fencing token that authorized
-- it.  The token prevents a stale scheduler instance from attaching a child
-- process after a newer scheduler has acquired the global history lease.
DROP PROCEDURE IF EXISTS add_history_partition_fencing_column;
DELIMITER //
CREATE PROCEDURE add_history_partition_fencing_column()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema=DATABASE()
          AND table_name='bar_ingest_partition'
          AND column_name='fencing_token'
    ) THEN
        ALTER TABLE bar_ingest_partition
            ADD COLUMN fencing_token BIGINT NULL AFTER owner_instance_id;
    END IF;
END //
DELIMITER ;

CALL add_history_partition_fencing_column();
DROP PROCEDURE IF EXISTS add_history_partition_fencing_column;

INSERT INTO schema_migration (version, description)
VALUES ('019', 'history partition scheduler fencing token')
ON DUPLICATE KEY UPDATE description=VALUES(description);
