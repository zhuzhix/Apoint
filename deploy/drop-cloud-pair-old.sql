USE astock_monitor;

DROP TABLE IF EXISTS pair_trend_lifecycle_old_20260815;
DROP TABLE IF EXISTS pair_trend_hit_old_20260815;
DROP TABLE IF EXISTS pair_trend_event_old_20260815;

INSERT INTO dataset_stat_snapshot(dataset_name,row_count,is_exact,updated_at)
SELECT 'pair_trend_event',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM pair_trend_event
UNION ALL
SELECT 'pair_trend_hit',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM pair_trend_hit
UNION ALL
SELECT 'pair_trend_lifecycle',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM pair_trend_lifecycle
ON DUPLICATE KEY UPDATE row_count=VALUES(row_count),is_exact=VALUES(is_exact),updated_at=VALUES(updated_at);
