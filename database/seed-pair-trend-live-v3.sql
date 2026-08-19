USE astock_monitor;

-- 在 StrategyScanner 首次启用 V3 前执行：以最近一次成功的全市场回放为实时状态基线。
-- 仅复制仍有效的价位，不把历史“观察/重点/成立”重新推送给网页。
SET @run_id := (
    SELECT id
    FROM pair_trend_backtest_run
    WHERE algorithm_version='pair-trend-v3'
      AND status='complete'
      AND requested_symbols>=1000
    ORDER BY date_to DESC,id DESC
    LIMIT 1
);

DELETE FROM pair_trend_event_outbox;
DELETE FROM pair_trend_live_lifecycle;
DELETE FROM pair_trend_live_hit;
DELETE FROM pair_trend_live_event;
DELETE FROM pair_trend_processed_event;
DELETE FROM pair_trend_consumer_checkpoint;

INSERT INTO pair_trend_live_event
    (event_key,symbol,symbol_name,pivot_type,status,first_seen_at,last_seen_at,
     confirmed_at,latest_pair_price,price_ticks,latest_pair_code,latest_pair_kind,
     timeframe_mask,frequencies,strongest_frequency,confluence_count,total_hit_count,
     confirmed_hit_count,invalidated_hit_count,pending_hit_count,retracted_hit_count,
     round_00_hit_count,double_digit_hit_count,score,max_trend_strength,
     algorithm_version,event_revision,content_hash,last_source_event_id,summary_json,
     stage,generation,is_active,discovered_at,observed_at,focused_at,established_at,
     invalidated_at,invalidated_price,invalidation_reason,root_5m_bob,root_5m_eob,
     last_transition_at)
SELECT
     event_key,symbol,symbol_name,pivot_type,status,first_seen_at,last_seen_at,
     confirmed_at,latest_pair_price,price_ticks,latest_pair_code,latest_pair_kind,
     timeframe_mask,frequencies,strongest_frequency,confluence_count,total_hit_count,
     confirmed_hit_count,invalidated_hit_count,pending_hit_count,0,
     round_00_hit_count,double_digit_hit_count,score,max_trend_strength,
     algorithm_version,0,SHA2(CAST(summary_json AS CHAR),256),
     CONCAT('bootstrap-run-',@run_id),summary_json,
     stage,generation,is_active,discovered_at,observed_at,focused_at,established_at,
     invalidated_at,invalidated_price,invalidation_reason,root_5m_bob,root_5m_eob,
     last_transition_at
FROM pair_trend_event
WHERE run_id=@run_id AND is_active=TRUE;

INSERT INTO pair_trend_live_hit
    (event_id,hit_key,symbol,frequency,trading_date,bob,eob,observed_at,confirmed_at,
     pivot_type,status,pair_price,price_ticks,pair_code,pair_kind,hit_field,
     trend_direction,trend_strength,ema20,ema60,atr14,previous_close,open_price,
     high_price,low_price,close_price,volume,amount,is_rolling_extreme,
     volume_percentile,wick_ratio,reversal_atr,score,confirmation_reason,
     source_revision,source_row_hash,source_event_id,algorithm_version,details_json,
     stage,is_promotion)
SELECT
     le.id,h.hit_key,h.symbol,h.frequency,h.trading_date,h.bob,h.eob,h.observed_at,
     h.confirmed_at,h.pivot_type,h.status,h.pair_price,h.price_ticks,h.pair_code,
     h.pair_kind,h.hit_field,h.trend_direction,h.trend_strength,h.ema20,h.ema60,
     h.atr14,h.previous_close,h.open_price,h.high_price,h.low_price,h.close_price,
     h.volume,h.amount,h.is_rolling_extreme,h.volume_percentile,h.wick_ratio,
     h.reversal_atr,h.score,h.confirmation_reason,0,h.source_row_hash,
     CONCAT('bootstrap-run-',@run_id),h.algorithm_version,h.details_json,
     h.stage,h.is_promotion
FROM pair_trend_hit h
JOIN pair_trend_event e ON e.id=h.event_id AND e.run_id=h.run_id
JOIN pair_trend_live_event le ON le.event_key=e.event_key
WHERE h.run_id=@run_id AND e.is_active=TRUE;

INSERT INTO pair_trend_live_lifecycle
    (event_id,lifecycle_key,symbol,from_stage,to_stage,occurred_at,
     trigger_frequency,trigger_price,reason,source_row_hash,should_notify)
SELECT
    le.id,l.lifecycle_key,l.symbol,l.from_stage,l.to_stage,l.occurred_at,
    l.trigger_frequency,l.trigger_price,l.reason,l.source_row_hash,FALSE
FROM pair_trend_lifecycle l
JOIN pair_trend_event e ON e.id=l.event_id AND e.run_id=l.run_id
JOIN pair_trend_live_event le ON le.event_key=e.event_key
WHERE l.run_id=@run_id AND e.is_active=TRUE;

SELECT @run_id AS source_run_id,
       (SELECT COUNT(*) FROM pair_trend_live_event) AS active_events,
       (SELECT COUNT(*) FROM pair_trend_live_hit) AS active_hits,
       (SELECT COUNT(*) FROM pair_trend_live_lifecycle) AS lifecycle_rows;
