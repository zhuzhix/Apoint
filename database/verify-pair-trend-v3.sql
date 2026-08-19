USE astock_monitor;

SET @run_id := (
    SELECT id FROM pair_trend_backtest_run
    WHERE algorithm_version='pair-trend-v3' AND status='complete' AND requested_symbols>=1000
    ORDER BY date_to DESC,id DESC LIMIT 1
);

SELECT r.id,r.status,r.requested_symbols,r.completed_symbols,r.failed_symbols,
       r.bars_processed,r.hits_detected,r.events_written,r.date_from,r.date_to
FROM pair_trend_backtest_run r WHERE r.id=@run_id;

SELECT stage,COUNT(*) event_count,COUNT(DISTINCT symbol) symbol_count,
       SUM(is_active) active_count
FROM pair_trend_event
WHERE run_id=@run_id
GROUP BY stage
ORDER BY FIELD(stage,'DISCOVERED','OBSERVING','FOCUS','ESTABLISHED','INVALIDATED');

SELECT pivot_type,latest_pair_kind,COUNT(*) event_count
FROM pair_trend_event
WHERE run_id=@run_id
GROUP BY pivot_type,latest_pair_kind
ORDER BY pivot_type,latest_pair_kind;

-- 以下检查均应返回 0。
SELECT 'duplicate_active_level' check_name,COUNT(*) violations
FROM (
    SELECT symbol,pivot_type,price_ticks
    FROM pair_trend_event
    WHERE run_id=@run_id AND is_active=TRUE
    GROUP BY symbol,pivot_type,price_ticks
    HAVING COUNT(*)>1
) duplicate_levels
UNION ALL
SELECT 'active_invalidated_stage',COUNT(*)
FROM pair_trend_event
WHERE run_id=@run_id AND is_active=TRUE AND stage='INVALIDATED'
UNION ALL
SELECT 'inactive_noninvalidated_stage',COUNT(*)
FROM pair_trend_event
WHERE run_id=@run_id AND is_active=FALSE AND stage<>'INVALIDATED'
UNION ALL
SELECT 'observing_without_30m_time',COUNT(*)
FROM pair_trend_event
WHERE run_id=@run_id AND stage IN ('OBSERVING','FOCUS','ESTABLISHED')
  AND observed_at IS NULL
UNION ALL
SELECT 'focus_without_60m_time',COUNT(*)
FROM pair_trend_event
WHERE run_id=@run_id AND stage IN ('FOCUS','ESTABLISHED')
  AND focused_at IS NULL
UNION ALL
SELECT 'established_without_day_time',COUNT(*)
FROM pair_trend_event
WHERE run_id=@run_id AND stage='ESTABLISHED' AND established_at IS NULL
UNION ALL
SELECT 'top_invalidated_without_strict_break',COUNT(*)
FROM pair_trend_event
WHERE run_id=@run_id AND pivot_type='TOP' AND stage='INVALIDATED'
  AND invalidated_price<=latest_pair_price
UNION ALL
SELECT 'bottom_invalidated_without_strict_break',COUNT(*)
FROM pair_trend_event
WHERE run_id=@run_id AND pivot_type='BOTTOM' AND stage='INVALIDATED'
  AND invalidated_price>=latest_pair_price
UNION ALL
SELECT 'discovery_not_5m',COUNT(*)
FROM pair_trend_hit
WHERE run_id=@run_id AND stage='DISCOVERED' AND frequency<>'5m'
UNION ALL
SELECT 'observing_not_30m',COUNT(*)
FROM pair_trend_hit
WHERE run_id=@run_id AND stage='OBSERVING' AND frequency<>'30m'
UNION ALL
SELECT 'focus_not_60m',COUNT(*)
FROM pair_trend_hit
WHERE run_id=@run_id AND stage='FOCUS' AND frequency<>'60m'
UNION ALL
SELECT 'established_not_1d',COUNT(*)
FROM pair_trend_hit
WHERE run_id=@run_id AND stage='ESTABLISHED' AND frequency<>'1d'
UNION ALL
SELECT 'discovery_should_notify',COUNT(*)
FROM pair_trend_lifecycle
WHERE run_id=@run_id AND to_stage='DISCOVERED' AND should_notify=TRUE;

SELECT COUNT(*) round_00_events
FROM pair_trend_event
WHERE run_id=@run_id AND latest_pair_kind='ROUND_00';
