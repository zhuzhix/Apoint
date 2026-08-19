USE astock_monitor;

START TRANSACTION;

SET @collector_id = 'local-pair-kline-01';

SELECT COUNT(*),COUNT(DISTINCT reason) INTO @matching,@distinct_reasons
FROM pair_trend_symbol_blacklist
WHERE collector_id = @collector_id
  AND blacklisted_at >= '2026-08-18 07:07:00'
  AND blacklisted_at <  '2026-08-18 07:18:00'
  AND reason LIKE '1d:%'
  AND reason LIKE '%expected=1 actual=0%'
  AND reason LIKE '%2026-08-18T15:00:00%';

-- Refuse a partial or expanded cleanup scope. The temporary CHECK makes the
-- transaction fail before DELETE if the read-only scope no longer matches.
CREATE TEMPORARY TABLE cleanup_daily_blacklist_gate (
    actual INT NOT NULL,
    distinct_reasons INT NOT NULL,
    CONSTRAINT chk_cleanup_daily_blacklist_count CHECK (
        actual = 4999 AND distinct_reasons = 1)
);
INSERT INTO cleanup_daily_blacklist_gate(actual,distinct_reasons)
VALUES (@matching,@distinct_reasons);

DELETE FROM pair_trend_symbol_blacklist
WHERE collector_id = @collector_id
  AND blacklisted_at >= '2026-08-18 07:07:00'
  AND blacklisted_at <  '2026-08-18 07:18:00'
  AND reason LIKE '1d:%'
  AND reason LIKE '%expected=1 actual=0%'
  AND reason LIKE '%2026-08-18T15:00:00%';

SELECT ROW_COUNT() INTO @deleted;
INSERT INTO cleanup_daily_blacklist_gate(actual,distinct_reasons)
VALUES (@deleted,1);

COMMIT;

SELECT @matching AS matched_rows,
       @deleted AS deleted_rows,
       (SELECT COUNT(*) FROM pair_trend_symbol_blacklist
        WHERE expires_at > UTC_TIMESTAMP(6)) AS active_remaining;
