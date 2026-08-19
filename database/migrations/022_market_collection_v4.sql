USE astock_monitor;

-- V4 recovery workers claim up to 50 symbols sharing one official SDK request
-- window. Keep that lookup indexed while preserving per-symbol durable state.
ALTER TABLE market_recovery_item
    ADD KEY ix_market_recovery_item_v4_batch
        (recovery_run_id,frequency,gap_start,gap_end,status,lease_expires_at);

INSERT INTO schema_migration (version,description)
VALUES ('022','market collection v4 batched official K-line recovery claims')
ON DUPLICATE KEY UPDATE description=VALUES(description);
