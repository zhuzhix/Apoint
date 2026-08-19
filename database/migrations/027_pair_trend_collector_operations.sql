USE astock_monitor;

-- Python 采集端只通过 WebAPI 上报运行状态；表中不保存 Token、API Key、
-- Redis/MySQL 连接串或其他凭据。
CREATE TABLE IF NOT EXISTS pair_trend_collector_heartbeat (
    collector_id VARCHAR(96) NOT NULL,
    instance_id VARCHAR(96) NOT NULL,
    status VARCHAR(24) NOT NULL,
    processes_expected INT NOT NULL DEFAULT 6,
    processes_running INT NOT NULL DEFAULT 0,
    active_jobs INT NOT NULL DEFAULT 0,
    queued_jobs INT NOT NULL DEFAULT 0,
    succeeded_jobs BIGINT NOT NULL DEFAULT 0,
    retrying_jobs BIGINT NOT NULL DEFAULT 0,
    failed_jobs BIGINT NOT NULL DEFAULT 0,
    blacklisted_symbols INT NOT NULL DEFAULT 0,
    cycles_completed BIGINT NOT NULL DEFAULT 0,
    current_cycle_id VARCHAR(64) NULL,
    host_name VARCHAR(128) NULL,
    app_version VARCHAR(64) NULL,
    started_at DATETIME(6) NULL,
    workers_json JSON NOT NULL,
    last_error VARCHAR(1024) NULL,
    last_error_at DATETIME(6) NULL,
    last_seen_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (collector_id),
    KEY ix_pair_collector_last_seen (last_seen_at),
    KEY ix_pair_collector_status (status,last_seen_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 黑名单以证券为全局唯一键。过期项无需立即删除，计划查询只排除 expires_at
-- 仍在未来的记录，便于运维页面审计最近失败。
CREATE TABLE IF NOT EXISTS pair_trend_symbol_blacklist (
    symbol VARCHAR(32) NOT NULL,
    collector_id VARCHAR(96) NOT NULL,
    failure_count INT NOT NULL,
    reason VARCHAR(1024) NOT NULL,
    blacklisted_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (symbol),
    KEY ix_pair_blacklist_expiry (expires_at),
    KEY ix_pair_blacklist_collector (collector_id,blacklisted_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO schema_migration(version,description)
VALUES ('027','pair trend collector heartbeat and one-day symbol blacklist')
ON DUPLICATE KEY UPDATE description=VALUES(description);
