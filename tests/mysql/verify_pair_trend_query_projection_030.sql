USE astock_monitor;

SET @AlgorithmVersion='pair-trend-v3';
SET @DateFrom='2026-08-17 00:00:00.000000';
SET @DateToExclusive='2026-08-18 00:00:00.000000';
SET @StatusAtExclusive=@DateToExclusive;
SET @PageSize=20;
SET @Offset=0;

DROP TEMPORARY TABLE IF EXISTS projection_verification_failures;
DROP TEMPORARY TABLE IF EXISTS canonical_group_result;
DROP TEMPORARY TABLE IF EXISTS projection_group_result;
CREATE TEMPORARY TABLE projection_verification_failures(
    check_name VARCHAR(96) NOT NULL,
    details VARCHAR(512) NOT NULL
);

INSERT INTO projection_verification_failures
SELECT 'source_projection_count',CONCAT(source_count,'<>',projection_count)
FROM (
    SELECT
      (SELECT COUNT(*) FROM pair_trend_live_event
       WHERE algorithm_version=@AlgorithmVersion AND root_5m_eob IS NOT NULL) source_count,
      (SELECT COUNT(*) FROM pair_trend_query_event
       WHERE algorithm_version=@AlgorithmVersion) projection_count
) counts
WHERE source_count<>projection_count;

INSERT INTO projection_verification_failures
SELECT 'source_projection_after_image',CONCAT('mismatch=',COUNT(*))
FROM pair_trend_live_event source
JOIN pair_trend_query_event projection ON projection.event_id=source.id
WHERE source.algorithm_version=@AlgorithmVersion AND (
    NOT(projection.event_key<=>source.event_key) OR
    NOT(projection.symbol<=>source.symbol) OR
    NOT(projection.symbol_name<=>source.symbol_name) OR
    NOT(projection.root_5m_eob<=>source.root_5m_eob) OR
    NOT(projection.pivot_type<=>source.pivot_type) OR
    NOT(projection.frequencies<=>source.frequencies) OR
    projection.frequency_mask<>(
      IF(FIND_IN_SET('5m',source.frequencies)>0,1,0) |
      IF(FIND_IN_SET('30m',source.frequencies)>0,2,0) |
      IF(FIND_IN_SET('60m',source.frequencies)>0,4,0) |
      IF(FIND_IN_SET('1d',source.frequencies)>0,8,0)) OR
    NOT(projection.observed_at<=>source.observed_at) OR
    NOT(projection.focused_at<=>source.focused_at) OR
    NOT(projection.established_at<=>source.established_at) OR
    NOT(projection.invalidated_at<=>source.invalidated_at) OR
    NOT(projection.current_stage<=>source.stage) OR
    NOT(projection.current_is_active<=>source.is_active) OR
    NOT(projection.source_revision<=>source.event_revision) OR
    NOT(projection.source_content_hash<=>source.content_hash))
HAVING COUNT(*)<>0;

CREATE TEMPORARY TABLE canonical_group_result AS
WITH filtered AS (
    SELECT e.symbol,e.root_5m_eob,e.pivot_type,
       CASE
         WHEN e.invalidated_at IS NOT NULL AND e.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
         WHEN e.established_at IS NOT NULL AND e.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
         WHEN e.focused_at IS NOT NULL AND e.focused_at<@StatusAtExclusive THEN 'FOCUS'
         WHEN e.observed_at IS NOT NULL AND e.observed_at<@StatusAtExclusive THEN 'OBSERVING'
         ELSE 'DISCOVERED'
       END stage_at_end
    FROM pair_trend_live_event e
    WHERE e.algorithm_version=@AlgorithmVersion AND e.root_5m_eob IS NOT NULL
      AND e.root_5m_eob>=@DateFrom AND e.root_5m_eob<@DateToExclusive
), grouped AS (
    SELECT symbol,MAX(root_5m_eob) LatestPivotAt,
      MAX(CASE WHEN pivot_type='TOP' THEN root_5m_eob END) LatestTopAt,
      MAX(CASE WHEN pivot_type='BOTTOM' THEN root_5m_eob END) LatestBottomAt,
      COUNT(*) EventCount,SUM(pivot_type='TOP') TopCount,
      SUM(pivot_type='BOTTOM') BottomCount,
      SUM(stage_at_end<>'INVALIDATED') ActiveAtEndCount,
      SUM(stage_at_end='INVALIDATED') InvalidatedAtEndCount
    FROM filtered GROUP BY symbol
), paged AS (
    SELECT grouped.*,COUNT(*) OVER() TotalGroups
    FROM grouped ORDER BY LatestPivotAt DESC,symbol ASC LIMIT 20 OFFSET 0
)
SELECT paged.TotalGroups,paged.symbol Symbol,latest.symbol_name SymbolName,
       paged.LatestPivotAt,paged.LatestTopAt,paged.LatestBottomAt,
       CASE
         WHEN latest.invalidated_at IS NOT NULL AND latest.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
         WHEN latest.established_at IS NOT NULL AND latest.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
         WHEN latest.focused_at IS NOT NULL AND latest.focused_at<@StatusAtExclusive THEN 'FOCUS'
         WHEN latest.observed_at IS NOT NULL AND latest.observed_at<@StatusAtExclusive THEN 'OBSERVING'
         ELSE 'DISCOVERED'
       END LatestStageAtEnd,
       paged.EventCount,paged.TopCount,paged.BottomCount,
       paged.ActiveAtEndCount,paged.InvalidatedAtEndCount
FROM paged
LEFT JOIN LATERAL (
    SELECT candidate.symbol_name,candidate.invalidated_at,candidate.established_at,
           candidate.focused_at,candidate.observed_at
    FROM pair_trend_live_event candidate FORCE INDEX(ix_pair_trend_live_symbol_period)
    WHERE candidate.algorithm_version=@AlgorithmVersion AND candidate.root_5m_eob IS NOT NULL
      AND candidate.root_5m_eob>=@DateFrom AND candidate.root_5m_eob<@DateToExclusive
      AND candidate.symbol=paged.symbol AND candidate.root_5m_eob=paged.LatestPivotAt
    ORDER BY candidate.id DESC LIMIT 1
) latest ON TRUE;

CREATE TEMPORARY TABLE projection_group_result AS
WITH filtered AS (
    SELECT e.symbol,e.root_5m_eob,e.pivot_type,
       CASE
         WHEN e.invalidated_at IS NOT NULL AND e.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
         WHEN e.established_at IS NOT NULL AND e.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
         WHEN e.focused_at IS NOT NULL AND e.focused_at<@StatusAtExclusive THEN 'FOCUS'
         WHEN e.observed_at IS NOT NULL AND e.observed_at<@StatusAtExclusive THEN 'OBSERVING'
         ELSE 'DISCOVERED'
       END stage_at_end
    FROM pair_trend_query_event e FORCE INDEX(ix_pair_trend_query_period)
    WHERE e.algorithm_version=@AlgorithmVersion AND e.root_5m_eob IS NOT NULL
      AND e.root_5m_eob>=@DateFrom AND e.root_5m_eob<@DateToExclusive
), grouped AS (
    SELECT symbol,MAX(root_5m_eob) LatestPivotAt,
      MAX(CASE WHEN pivot_type='TOP' THEN root_5m_eob END) LatestTopAt,
      MAX(CASE WHEN pivot_type='BOTTOM' THEN root_5m_eob END) LatestBottomAt,
      COUNT(*) EventCount,SUM(pivot_type='TOP') TopCount,
      SUM(pivot_type='BOTTOM') BottomCount,
      SUM(stage_at_end<>'INVALIDATED') ActiveAtEndCount,
      SUM(stage_at_end='INVALIDATED') InvalidatedAtEndCount
    FROM filtered GROUP BY symbol
), paged AS (
    SELECT grouped.*,COUNT(*) OVER() TotalGroups
    FROM grouped ORDER BY LatestPivotAt DESC,symbol ASC LIMIT 20 OFFSET 0
)
SELECT paged.TotalGroups,paged.symbol Symbol,latest.symbol_name SymbolName,
       paged.LatestPivotAt,paged.LatestTopAt,paged.LatestBottomAt,
       CASE
         WHEN latest.invalidated_at IS NOT NULL AND latest.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
         WHEN latest.established_at IS NOT NULL AND latest.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
         WHEN latest.focused_at IS NOT NULL AND latest.focused_at<@StatusAtExclusive THEN 'FOCUS'
         WHEN latest.observed_at IS NOT NULL AND latest.observed_at<@StatusAtExclusive THEN 'OBSERVING'
         ELSE 'DISCOVERED'
       END LatestStageAtEnd,
       paged.EventCount,paged.TopCount,paged.BottomCount,
       paged.ActiveAtEndCount,paged.InvalidatedAtEndCount
FROM paged
LEFT JOIN LATERAL (
    SELECT candidate.symbol_name,candidate.invalidated_at,candidate.established_at,
           candidate.focused_at,candidate.observed_at
    FROM pair_trend_query_event candidate FORCE INDEX(ix_pair_trend_query_symbol_period)
    WHERE candidate.algorithm_version=@AlgorithmVersion AND candidate.root_5m_eob IS NOT NULL
      AND candidate.root_5m_eob>=@DateFrom AND candidate.root_5m_eob<@DateToExclusive
      AND candidate.symbol=paged.symbol AND candidate.root_5m_eob=paged.LatestPivotAt
    ORDER BY candidate.event_id DESC LIMIT 1
) latest ON TRUE;

INSERT INTO projection_verification_failures
SELECT 'group_page_count',CONCAT(canonical_count,'<>',projection_count)
FROM (
    SELECT (SELECT COUNT(*) FROM canonical_group_result) canonical_count,
           (SELECT COUNT(*) FROM projection_group_result) projection_count
) counts
WHERE canonical_count<>projection_count;

INSERT INTO projection_verification_failures
SELECT 'group_page_fields',CONCAT('mismatch=',COUNT(*))
FROM canonical_group_result canonical
JOIN projection_group_result projection ON projection.Symbol=canonical.Symbol
WHERE NOT(projection.TotalGroups<=>canonical.TotalGroups)
   OR NOT(projection.SymbolName<=>canonical.SymbolName)
   OR NOT(projection.LatestPivotAt<=>canonical.LatestPivotAt)
   OR NOT(projection.LatestTopAt<=>canonical.LatestTopAt)
   OR NOT(projection.LatestBottomAt<=>canonical.LatestBottomAt)
   OR NOT(projection.LatestStageAtEnd<=>canonical.LatestStageAtEnd)
   OR NOT(projection.EventCount<=>canonical.EventCount)
   OR NOT(projection.TopCount<=>canonical.TopCount)
   OR NOT(projection.BottomCount<=>canonical.BottomCount)
   OR NOT(projection.ActiveAtEndCount<=>canonical.ActiveAtEndCount)
   OR NOT(projection.InvalidatedAtEndCount<=>canonical.InvalidatedAtEndCount)
HAVING COUNT(*)<>0;

SELECT 'projection_rows',COUNT(*) FROM pair_trend_query_event;
SELECT 'canonical_groups',COUNT(*),MAX(TotalGroups) FROM canonical_group_result;
SELECT 'projection_groups',COUNT(*),MAX(TotalGroups) FROM projection_group_result;
SELECT * FROM projection_verification_failures ORDER BY check_name;

CREATE TEMPORARY TABLE projection_verification_gate(
    failure_count INT NOT NULL CHECK(failure_count=0)
);
INSERT INTO projection_verification_gate
SELECT COUNT(*) FROM projection_verification_failures;

SELECT 'PASS: pair-trend query projection 030 verified' result;
