USE astock_monitor;

-- An active BOTTOM keeps its point-in-time wave signal after promotion from
-- FOCUS to ESTABLISHED.  The original focused_at remains the scoring anchor;
-- no post-establishment market data is admitted into the evaluation window.
START TRANSACTION;

-- Re-open only inconsistent or unfinished v2 jobs that already exist for the
-- newly covered ESTABLISHED set.  Valid completed results remain untouched.
UPDATE wave_bottom_collection_job job
JOIN pair_trend_live_event event ON event.id=job.event_id
SET job.symbol=event.symbol,
    job.focused_at=event.focused_at,
    job.data_end_date=DATE_SUB(DATE(event.focused_at),INTERVAL 1 DAY),
    job.required_daily_bars=120,job.adjust_mode='NONE',job.status='PENDING',
    job.attempt_count=0,job.next_attempt_at=NULL,job.last_error=NULL,
    job.completed_at=NULL,job.lease_token=NULL,job.lease_owner=NULL,
    job.lease_expires_at=NULL
WHERE job.algorithm_version='pair-wave-bottom-v2'
  AND event.algorithm_version='pair-trend-v3'
  AND event.pivot_type='BOTTOM' AND event.stage='ESTABLISHED'
  AND event.is_active=TRUE AND event.focused_at IS NOT NULL
  AND NOT(job.status='COMPLETED'
      AND event.wave_calculation_status IN ('COMPLETED','INSUFFICIENT_DATA')
      AND COALESCE(event.wave_algorithm_version,'')='pair-wave-bottom-v2');

-- Create the durable work that the former FOCUS-only rule omitted.
INSERT INTO wave_bottom_collection_job(
    event_id,symbol,focused_at,data_end_date,required_daily_bars,
    adjust_mode,algorithm_version,status)
SELECT event.id,event.symbol,event.focused_at,
       DATE_SUB(DATE(event.focused_at),INTERVAL 1 DAY),120,
       'NONE','pair-wave-bottom-v2','PENDING'
FROM pair_trend_live_event event
LEFT JOIN wave_bottom_collection_job job
  ON job.event_id=event.id
 AND job.algorithm_version='pair-wave-bottom-v2'
WHERE event.algorithm_version='pair-trend-v3'
  AND event.pivot_type='BOTTOM' AND event.stage='ESTABLISHED'
  AND event.is_active=TRUE AND event.focused_at IS NOT NULL
  AND job.id IS NULL;

-- Clear only stale/non-final materializations. Valid v2 results are retained
-- byte-for-byte and are never requeued merely because the event was promoted.
UPDATE pair_trend_live_event event
JOIN wave_bottom_collection_job job
  ON job.event_id=event.id
 AND job.algorithm_version='pair-wave-bottom-v2'
SET event.wave_calculation_status='PENDING',event.wave_signal=NULL,
    event.wave_score=NULL,event.wave_evaluated_at=NULL,
    event.wave_data_as_of=NULL,event.wave_algorithm_version=NULL,
    event.wave_input_hash=NULL,event.wave_components=NULL,
    event.wave_revision=event.wave_revision+1
WHERE event.algorithm_version='pair-trend-v3'
  AND event.pivot_type='BOTTOM' AND event.stage='ESTABLISHED'
  AND event.is_active=TRUE AND event.focused_at=job.focused_at
  AND NOT(job.status='COMPLETED'
      AND event.wave_calculation_status IN ('COMPLETED','INSUFFICIENT_DATA')
      AND COALESCE(event.wave_algorithm_version,'')='pair-wave-bottom-v2');

INSERT INTO schema_migration(version,description)
VALUES ('033','retain and backfill v2 wave signals for active established bottoms')
ON DUPLICATE KEY UPDATE description=VALUES(description);

COMMIT;
