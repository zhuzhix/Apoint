USE astock_monitor;

RENAME TABLE
    pair_trend_lifecycle TO pair_trend_lifecycle_old_20260815,
    pair_trend_hit TO pair_trend_hit_old_20260815,
    pair_trend_event TO pair_trend_event_old_20260815,
    pair_trend_event_compact_20260815 TO pair_trend_event,
    pair_trend_hit_compact_20260815 TO pair_trend_hit,
    pair_trend_lifecycle_compact_20260815 TO pair_trend_lifecycle;

SELECT 'post_swap_counts' AS check_name,
       (SELECT COUNT(*) FROM pair_trend_event) AS event_count,
       (SELECT COUNT(*) FROM pair_trend_hit) AS hit_count,
       (SELECT COUNT(*) FROM pair_trend_lifecycle) AS lifecycle_count;

CHECK TABLE pair_trend_event, pair_trend_hit, pair_trend_lifecycle;
