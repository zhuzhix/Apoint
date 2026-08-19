USE astock_monitor;

-- Durable audit for operator-initiated state repairs and cancellations.
CREATE TABLE IF NOT EXISTS market_operation_audit (
    id BIGINT NOT NULL AUTO_INCREMENT,
    operation_type VARCHAR(32) NOT NULL,
    target_type VARCHAR(64) NOT NULL,
    target_id VARCHAR(160) NOT NULL,
    requested_by VARCHAR(128) NOT NULL,
    reason VARCHAR(2000) NULL,
    result VARCHAR(32) NOT NULL,
    details_json JSON NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    KEY ix_market_operation_target (target_type,target_id,created_at),
    KEY ix_market_operation_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Repair the provider's 2038 sentinel that was previously persisted as a real
-- delisting date. Future-dated genuine delist announcements remain active until
-- their effective date.
UPDATE instrument
SET delist_date=NULL,status='active'
WHERE delist_date>='2038-01-01';
UPDATE instrument
SET status=CASE
    WHEN delist_date IS NULL OR delist_date>CURRENT_DATE() THEN 'active'
    ELSE 'delisted'
END;

CREATE TABLE IF NOT EXISTS dataset_stat_snapshot (
    dataset_name VARCHAR(64) NOT NULL,
    row_count BIGINT NOT NULL DEFAULT 0,
    is_exact BOOLEAN NOT NULL DEFAULT FALSE,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (dataset_name),
    KEY ix_dataset_stat_updated (updated_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO dataset_stat_snapshot(dataset_name,row_count,is_exact)
SELECT 'instrument_daily_status',COALESCE(table_rows,0),FALSE
FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='instrument_daily_status'
ON DUPLICATE KEY UPDATE row_count=VALUES(row_count),is_exact=FALSE,updated_at=UTC_TIMESTAMP(6);
INSERT INTO dataset_stat_snapshot(dataset_name,row_count,is_exact)
SELECT 'kline_bar_5m',COALESCE(SUM(table_rows),0),FALSE
FROM information_schema.partitions WHERE table_schema=DATABASE() AND table_name='kline_bar_5m'
ON DUPLICATE KEY UPDATE row_count=VALUES(row_count),is_exact=FALSE,updated_at=UTC_TIMESTAMP(6);
INSERT INTO dataset_stat_snapshot(dataset_name,row_count,is_exact)
SELECT 'kline_bar_daily',COALESCE(SUM(table_rows),0),FALSE
FROM information_schema.partitions WHERE table_schema=DATABASE() AND table_name='kline_bar_daily'
ON DUPLICATE KEY UPDATE row_count=VALUES(row_count),is_exact=FALSE,updated_at=UTC_TIMESTAMP(6);

INSERT INTO schema_migration (version, description)
VALUES ('018', 'system remediation audit and safe recovery cancellation')
ON DUPLICATE KEY UPDATE description=VALUES(description);
