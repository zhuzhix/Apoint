USE astock_monitor;

-- pair-wave-bottom-v3 adds a 10-point confirmation that the latest completed
-- daily close both breaks the previous 10-session high and stands above MA5.
-- Thresholds are 70/85.  The point-in-time window is still anchored to the
-- immutable focused_at date, so events that were invalidated later can be
-- recomputed without admitting future market data.
START TRANSACTION;

INSERT INTO wave_bottom_collection_job(
    event_id,symbol,focused_at,data_end_date,required_daily_bars,
    adjust_mode,algorithm_version,status)
SELECT event.id,event.symbol,event.focused_at,
       DATE_SUB(DATE(event.focused_at),INTERVAL 1 DAY),120,
       'NONE','pair-wave-bottom-v3','PENDING'
FROM pair_trend_live_event event
WHERE event.algorithm_version='pair-trend-v3'
  AND event.pivot_type='BOTTOM'
  AND event.focused_at IS NOT NULL
  AND (
      EXISTS (
          SELECT 1
          FROM wave_bottom_collection_job old_job
          WHERE old_job.event_id=event.id
            AND old_job.algorithm_version='pair-wave-bottom-v2'
            AND old_job.status='COMPLETED'
      )
      OR (
          event.stage IN ('FOCUS','ESTABLISHED')
          AND event.is_active=TRUE
      )
  )
ON DUPLICATE KEY UPDATE event_id=VALUES(event_id);

-- Do not erase a valid v2 result before its v3 replacement is ready.  Claiming
-- a v3 job changes only that event to COLLECTING; successful completion then
-- atomically replaces score, components and algorithm version.
INSERT INTO schema_migration(version,description)
VALUES ('035','recalculate all wave signals with pair-wave-bottom-v3 scoring')
ON DUPLICATE KEY UPDATE description=VALUES(description);

COMMIT;
