USE astock_monitor;

-- pair-wave-bottom-v2 is the sole production definition: six components,
-- maximum score 90, CANDIDATE 60-74 and STRONG 75-90.  Old queued work is
-- never allowed to run under the current scorer.
START TRANSACTION;

UPDATE wave_bottom_collection_job
SET status='SUPERSEDED',lease_token=NULL,lease_owner=NULL,
    lease_expires_at=NULL,next_attempt_at=NULL,
    last_error='superseded by canonical pair-wave-bottom-v2 backfill';

-- Remove every previously materialized wave result before reusing the v2
-- version name.  Only the exact eligible set below is allowed to become
-- PENDING again, so different v2 threshold definitions cannot coexist.
UPDATE pair_trend_live_event
SET wave_calculation_status='NOT_ELIGIBLE',wave_signal=NULL,wave_score=NULL,
    wave_evaluated_at=NULL,wave_data_as_of=NULL,wave_algorithm_version=NULL,
    wave_input_hash=NULL,wave_components=NULL,wave_revision=wave_revision+1
WHERE wave_calculation_status<>'NOT_ELIGIBLE'
   OR wave_signal IS NOT NULL OR wave_score IS NOT NULL
   OR wave_algorithm_version IS NOT NULL OR wave_input_hash IS NOT NULL
   OR wave_components IS NOT NULL;

INSERT INTO wave_bottom_collection_job(
    event_id,symbol,focused_at,data_end_date,required_daily_bars,
    adjust_mode,algorithm_version,status)
SELECT event.id,event.symbol,event.focused_at,
       DATE_SUB(DATE(event.focused_at),INTERVAL 1 DAY),120,
       'NONE','pair-wave-bottom-v2','PENDING'
FROM pair_trend_live_event event
WHERE event.algorithm_version='pair-trend-v3'
  AND event.pivot_type='BOTTOM' AND event.stage='FOCUS'
  AND event.is_active=TRUE AND event.focused_at IS NOT NULL
ON DUPLICATE KEY UPDATE
    symbol=VALUES(symbol),focused_at=VALUES(focused_at),
    data_end_date=VALUES(data_end_date),required_daily_bars=120,
    adjust_mode='NONE',status='PENDING',attempt_count=0,
    next_attempt_at=NULL,last_error=NULL,completed_at=NULL,
    lease_token=NULL,lease_owner=NULL,lease_expires_at=NULL;

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
  AND event.pivot_type='BOTTOM' AND event.stage='FOCUS'
  AND event.is_active=TRUE AND event.focused_at=job.focused_at;

INSERT INTO schema_migration(version,description)
VALUES ('032','canonical v2 wave score and active FOCUS bottom backfill')
ON DUPLICATE KEY UPDATE description=VALUES(description);

COMMIT;
