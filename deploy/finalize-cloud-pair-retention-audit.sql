USE astock_monitor;

INSERT INTO pair_trend_retention_run
    (cutoff_date,status,planned_event_count,planned_hit_count,
     planned_lifecycle_count,deleted_event_count,last_event_id,max_event_id,
     started_at,finished_at,error_message)
SELECT
    '2026-07-01','complete',
    3036006,5473176,7106767,3036006,4257627,4257627,
    UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),
    'Completed by validated keep-data rebuild; old rows removed before 2026-07-01'
WHERE NOT EXISTS (
    SELECT 1
    FROM pair_trend_retention_run
    WHERE cutoff_date='2026-07-01' AND status='complete'
);
