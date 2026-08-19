-- Pair-trend grouped-query phase 1 semantic verification (MySQL 8.0+).
--
-- Safety contract:
--   * Every table created by this script is TEMPORARY and session-scoped.
--   * The production pair_trend_live_event table is only used as the LIKE source.
--   * No production row is read, inserted, updated, or deleted.
--   * Disconnecting the mysql session removes the entire fixture automatically.
--   * MySQL 1137 guard: one statement must reference each physical TEMPORARY
--     table at most once. Materialize reused totals in session variables or use
--     the paired fixture clone; never add a TEMPORARY-table self-join/subquery.
--     A CTE whose ancestry reads a TEMPORARY table must also have exactly one
--     downstream FROM/JOIN consumer because the optimizer may inline that CTE.
--
-- Run from the repository root (credentials are intentionally not embedded):
--   mysql --database=astock_monitor --batch --raw \
--     --execute="source tests/mysql/verify_pair_trend_grouped_query_phase1.sql"
-- The account needs CREATE TEMPORARY TABLES and enough read permission to clone
-- the production table definition; it needs no DML permission on production rows.

USE astock_monitor;
SET NAMES latin1 COLLATE latin1_swedish_ci;

SET @AlgorithmVersion = 'pair-trend-v3';
SET @DateFrom = CAST('2026-08-17 00:00:00.000000' AS DATETIME(6));
SET @DateToExclusive = CAST('2026-08-18 00:00:00.000000' AS DATETIME(6));
SET @StatusAtExclusive = @DateToExclusive;

-- Make the script repeatable even when it is sourced more than once in one session.
DROP TEMPORARY TABLE IF EXISTS phase1_assertion_guard;
DROP TEMPORARY TABLE IF EXISTS phase1_failures;
DROP TEMPORARY TABLE IF EXISTS phase1_filter_expected;
DROP TEMPORARY TABLE IF EXISTS phase1_filter_result;
DROP TEMPORARY TABLE IF EXISTS phase1_evaluated;
DROP TEMPORARY TABLE IF EXISTS phase1_stage_expected;
DROP TEMPORARY TABLE IF EXISTS phase1_keyword_result;
DROP TEMPORARY TABLE IF EXISTS phase1_true_empty_result;
DROP TEMPORARY TABLE IF EXISTS phase1_page_result;
DROP TEMPORARY TABLE IF EXISTS phase1_optimized_result;
DROP TEMPORARY TABLE IF EXISTS phase1_legacy_result;
DROP TEMPORARY TABLE IF EXISTS phase1_numbers;
DROP TEMPORARY TABLE IF EXISTS pair_trend_live_event_fixture_latest;
DROP TEMPORARY TABLE IF EXISTS pair_trend_live_event_fixture;

-- LIKE keeps the fixture aligned with the deployed table's column types, indexes,
-- defaults, and CHECK constraints. The different name prevents shadowing mistakes.
CREATE TEMPORARY TABLE pair_trend_live_event_fixture
LIKE astock_monitor.pair_trend_live_event;

CREATE TEMPORARY TABLE phase1_numbers (
    n INT NOT NULL PRIMARY KEY
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

INSERT INTO phase1_numbers(n) VALUES
    (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),
    (11),(12),(13),(14),(15),(16),(17),(18),(19),(20),
    (21),(22),(23),(24),(25);

-- The 25 base rows deliberately produce exactly 25 stock groups. Rows 4-11
-- freeze the DATETIME(6) status-boundary matrix used later in this test.
INSERT INTO pair_trend_live_event_fixture (
    id,event_key,symbol,symbol_name,pivot_type,status,
    first_seen_at,last_seen_at,confirmed_at,
    latest_pair_price,latest_pair_code,latest_pair_kind,
    timeframe_mask,frequencies,strongest_frequency,confluence_count,total_hit_count,
    algorithm_version,stage,generation,is_active,
    discovered_at,observed_at,focused_at,established_at,invalidated_at,
    invalidation_reason,root_5m_bob,root_5m_eob,last_transition_at,
    content_hash,last_source_event_id,summary_json
)
SELECT
    1000+n,
    SHA2(CONCAT('phase1-base-',n),256),
    CONCAT('SHSE.',LPAD(600000+n,6,'0')),
    CONCAT('SampleStock',LPAD(n,2,'0')),
    IF(MOD(n,2)=1,'TOP','BOTTOM'),
    'active',
    DATE_ADD('2026-08-17 09:30:00.000000',INTERVAL n MINUTE),
    DATE_ADD('2026-08-17 09:30:00.000000',INTERVAL n MINUTE),
    NULL,
    10.000000+n/100.0,
    1,
    'DOUBLE_DIGIT',
    1,
    CASE n WHEN 13 THEN '5m,30m' WHEN 14 THEN '60m,1d' ELSE '5m' END,
    CASE n WHEN 14 THEN '1d' WHEN 13 THEN '30m' ELSE '5m' END,
    1,
    1,
    @AlgorithmVersion,
    CASE
        WHEN n=4 THEN 'OBSERVING'
        WHEN n=5 THEN 'FOCUS'
        WHEN n=6 THEN 'ESTABLISHED'
        WHEN n BETWEEN 7 AND 11 THEN 'INVALIDATED'
        ELSE 'DISCOVERED'
    END,
    1,
    IF(n BETWEEN 7 AND 11,FALSE,TRUE),
    DATE_ADD('2026-08-17 09:30:00.000000',INTERVAL n MINUTE),
    CASE WHEN n BETWEEN 4 AND 10
         THEN DATE_SUB(@StatusAtExclusive,INTERVAL 4 MICROSECOND) END,
    CASE
        WHEN n BETWEEN 5 AND 9 THEN DATE_SUB(@StatusAtExclusive,INTERVAL 3 MICROSECOND)
        WHEN n=10 THEN @StatusAtExclusive
    END,
    CASE
        WHEN n IN (6,7,9) THEN DATE_SUB(@StatusAtExclusive,INTERVAL 2 MICROSECOND)
        WHEN n=10 THEN DATE_ADD(@StatusAtExclusive,INTERVAL 1 MICROSECOND)
    END,
    CASE
        WHEN n=7 THEN DATE_SUB(@StatusAtExclusive,INTERVAL 1 MICROSECOND)
        WHEN n=8 THEN @StatusAtExclusive
        WHEN n IN (9,11) THEN DATE_ADD(@StatusAtExclusive,INTERVAL 1 MICROSECOND)
        WHEN n=10 THEN DATE_ADD(@StatusAtExclusive,INTERVAL 2 MICROSECOND)
    END,
    CASE WHEN n BETWEEN 7 AND 11 THEN 'fixture-current-invalidation' END,
    DATE_ADD('2026-08-17 09:25:00.000000',INTERVAL n MINUTE),
    DATE_ADD('2026-08-17 09:30:00.000000',INTERVAL n MINUTE),
    DATE_ADD('2026-08-17 09:30:00.000000',INTERVAL n MINUTE),
    SHA2(CONCAT('phase1-content-',n),256),
    CONCAT('phase1-source-',n),
    JSON_OBJECT('fixture','phase1','n',n)
FROM phase1_numbers;

-- Same root_5m_eob, different id: id=2002 must win for both name and stage.
INSERT INTO pair_trend_live_event_fixture (
    id,event_key,symbol,symbol_name,pivot_type,status,
    first_seen_at,last_seen_at,latest_pair_price,latest_pair_code,latest_pair_kind,
    timeframe_mask,frequencies,strongest_frequency,confluence_count,total_hit_count,
    algorithm_version,stage,generation,is_active,
    discovered_at,observed_at,focused_at,established_at,invalidated_at,
    invalidation_reason,root_5m_bob,root_5m_eob,last_transition_at,
    content_hash,last_source_event_id,summary_json
) VALUES
(
    2001,SHA2('phase1-tie-old',256),'SHSE.600001','TieOld','TOP','active',
    '2026-08-17 14:00:00.000000','2026-08-17 14:00:00.000000',10.010000,1,'DOUBLE_DIGIT',
    1,'5m','5m',1,1,@AlgorithmVersion,'FOCUS',2,TRUE,
    '2026-08-17 14:00:00.000000',DATE_SUB(@StatusAtExclusive,INTERVAL 3 MICROSECOND),
    DATE_SUB(@StatusAtExclusive,INTERVAL 2 MICROSECOND),NULL,NULL,NULL,
    '2026-08-17 13:55:00.000000','2026-08-17 14:00:00.000000','2026-08-17 14:00:00.000000',
    SHA2('phase1-tie-old-content',256),'phase1-tie-old-source',JSON_OBJECT('fixture','tie-old')
),
(
    2002,SHA2('phase1-tie-new',256),'SHSE.600001','TieLatest','TOP','active',
    '2026-08-17 14:00:00.000000','2026-08-17 14:00:00.000000',10.020000,1,'DOUBLE_DIGIT',
    2,'30m','30m',1,1,@AlgorithmVersion,'INVALIDATED',3,FALSE,
    '2026-08-17 14:00:00.000000',DATE_SUB(@StatusAtExclusive,INTERVAL 4 MICROSECOND),
    DATE_SUB(@StatusAtExclusive,INTERVAL 3 MICROSECOND),
    DATE_SUB(@StatusAtExclusive,INTERVAL 2 MICROSECOND),
    DATE_ADD(@StatusAtExclusive,INTERVAL 1 MICROSECOND),'fixture-after-cutoff',
    '2026-08-17 13:55:00.000000','2026-08-17 14:00:00.000000','2026-08-18 00:00:00.000001',
    SHA2('phase1-tie-new-content',256),'phase1-tie-new-source',JSON_OBJECT('fixture','tie-new')
);

-- Historical rename: keyword filtering must choose the newest event that also
-- matches the keyword, not the stock's unfiltered newest name.
INSERT INTO pair_trend_live_event_fixture (
    id,event_key,symbol,symbol_name,pivot_type,status,
    first_seen_at,last_seen_at,latest_pair_price,latest_pair_code,latest_pair_kind,
    timeframe_mask,frequencies,strongest_frequency,confluence_count,total_hit_count,
    algorithm_version,stage,generation,is_active,
    discovered_at,observed_at,focused_at,established_at,invalidated_at,
    root_5m_bob,root_5m_eob,last_transition_at,
    content_hash,last_source_event_id,summary_json
) VALUES
(
    2101,SHA2('phase1-rename-old',256),'SHSE.600002','HistoricalOldName','BOTTOM','active',
    '2026-08-17 10:00:00.000000','2026-08-17 10:00:00.000000',10.110000,1,'DOUBLE_DIGIT',
    1,'5m','5m',1,1,@AlgorithmVersion,'FOCUS',2,TRUE,
    '2026-08-17 10:00:00.000000',DATE_SUB(@StatusAtExclusive,INTERVAL 4 MICROSECOND),
    DATE_SUB(@StatusAtExclusive,INTERVAL 3 MICROSECOND),NULL,NULL,
    '2026-08-17 09:55:00.000000','2026-08-17 10:00:00.000000','2026-08-17 10:00:00.000000',
    SHA2('phase1-rename-old-content',256),'phase1-rename-old-source',JSON_OBJECT('fixture','rename-old')
),
(
    2102,SHA2('phase1-rename-new',256),'SHSE.600002','CurrentNewName','BOTTOM','active',
    '2026-08-17 11:00:00.000000','2026-08-17 11:00:00.000000',10.120000,1,'DOUBLE_DIGIT',
    4,'60m','60m',1,1,@AlgorithmVersion,'ESTABLISHED',3,TRUE,
    '2026-08-17 11:00:00.000000',DATE_SUB(@StatusAtExclusive,INTERVAL 4 MICROSECOND),
    DATE_SUB(@StatusAtExclusive,INTERVAL 3 MICROSECOND),
    DATE_SUB(@StatusAtExclusive,INTERVAL 2 MICROSECOND),NULL,
    '2026-08-17 10:55:00.000000','2026-08-17 11:00:00.000000','2026-08-17 11:00:00.000000',
    SHA2('phase1-rename-new-content',256),'phase1-rename-new-source',JSON_OBJECT('fixture','rename-new')
);

-- These rows prove that algorithm and exclusive date boundaries do not leak into
-- the expected 25 groups.
INSERT INTO pair_trend_live_event_fixture (
    id,event_key,symbol,symbol_name,pivot_type,status,
    first_seen_at,last_seen_at,latest_pair_price,latest_pair_code,latest_pair_kind,
    timeframe_mask,frequencies,strongest_frequency,confluence_count,total_hit_count,
    algorithm_version,stage,generation,is_active,discovered_at,
    root_5m_bob,root_5m_eob,last_transition_at,
    content_hash,last_source_event_id,summary_json
) VALUES
(
    3001,SHA2('phase1-exclusive-date',256),'SHSE.699998','ExclusiveBoundary','TOP','active',
    @DateToExclusive,@DateToExclusive,9.980000,1,'DOUBLE_DIGIT',1,'5m','5m',1,1,
    @AlgorithmVersion,'DISCOVERED',1,TRUE,@DateToExclusive,
    DATE_SUB(@DateToExclusive,INTERVAL 5 MINUTE),@DateToExclusive,@DateToExclusive,
    SHA2('phase1-exclusive-date-content',256),'phase1-exclusive-date-source',JSON_OBJECT('fixture','excluded-date')
),
(
    3002,SHA2('phase1-wrong-version',256),'SHSE.699999','LegacyAlgorithm','TOP','active',
    '2026-08-17 10:00:00.000000','2026-08-17 10:00:00.000000',9.990000,1,'DOUBLE_DIGIT',1,'5m','5m',1,1,
    'pair-trend-v2','DISCOVERED',1,TRUE,'2026-08-17 10:00:00.000000',
    '2026-08-17 09:55:00.000000','2026-08-17 10:00:00.000000','2026-08-17 10:00:00.000000',
    SHA2('phase1-wrong-version-content',256),'phase1-wrong-version-source',JSON_OBJECT('fixture','excluded-version')
);

-- MySQL cannot reopen one TEMPORARY table under multiple aliases in a single
-- statement. Production does not have that limitation, so keep an identical
-- second session-scoped clone solely for latest-candidate lookup in fixture SQL.
CREATE TEMPORARY TABLE pair_trend_live_event_fixture_latest
LIKE astock_monitor.pair_trend_live_event;

INSERT INTO pair_trend_live_event_fixture_latest
SELECT * FROM pair_trend_live_event_fixture;

CREATE TEMPORARY TABLE phase1_legacy_result (
    symbol VARCHAR(32) NOT NULL PRIMARY KEY,
    symbol_name VARCHAR(128) NULL,
    latest_pivot_at DATETIME(6) NOT NULL,
    latest_top_at DATETIME(6) NULL,
    latest_bottom_at DATETIME(6) NULL,
    latest_stage_at_end VARCHAR(24) NOT NULL,
    event_count BIGINT NOT NULL,
    top_count BIGINT NOT NULL,
    bottom_count BIGINT NOT NULL,
    active_at_end_count BIGINT NOT NULL,
    invalidated_at_end_count BIGINT NOT NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

-- FROZEN LEGACY SHAPE: rank every matching event before grouping.
INSERT INTO phase1_legacy_result
WITH filtered AS (
    SELECT e.id,e.symbol,e.symbol_name,e.root_5m_eob,e.pivot_type,
           CASE
               WHEN e.invalidated_at IS NOT NULL AND e.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
               WHEN e.established_at IS NOT NULL AND e.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
               WHEN e.focused_at IS NOT NULL AND e.focused_at<@StatusAtExclusive THEN 'FOCUS'
               WHEN e.observed_at IS NOT NULL AND e.observed_at<@StatusAtExclusive THEN 'OBSERVING'
               ELSE 'DISCOVERED'
           END stage_at_end
    FROM pair_trend_live_event_fixture e
    WHERE e.algorithm_version=@AlgorithmVersion
      AND e.root_5m_eob IS NOT NULL
      AND e.root_5m_eob>=@DateFrom
      AND e.root_5m_eob<@DateToExclusive
), ranked AS (
    SELECT filtered.*,
           ROW_NUMBER() OVER (
               PARTITION BY symbol ORDER BY root_5m_eob DESC,id DESC
           ) fixture_row_number
    FROM filtered
)
SELECT symbol,
       MAX(CASE WHEN fixture_row_number=1 THEN symbol_name END),
       MAX(root_5m_eob),
       MAX(CASE WHEN pivot_type='TOP' THEN root_5m_eob END),
       MAX(CASE WHEN pivot_type='BOTTOM' THEN root_5m_eob END),
       MAX(CASE WHEN fixture_row_number=1 THEN stage_at_end END),
       COUNT(*),SUM(pivot_type='TOP'),SUM(pivot_type='BOTTOM'),
       SUM(stage_at_end<>'INVALIDATED'),SUM(stage_at_end='INVALIDATED')
FROM ranked
GROUP BY symbol;

CREATE TEMPORARY TABLE phase1_optimized_result LIKE phase1_legacy_result;

-- FROZEN PHASE-1 SHAPE: aggregate first; locate the latest row only after the
-- stock set has been reduced. This mirrors BuildStockGroupSqlForAudit.
INSERT INTO phase1_optimized_result
WITH filtered AS (
    SELECT e.symbol,e.root_5m_eob,e.pivot_type,
           CASE
               WHEN e.invalidated_at IS NOT NULL AND e.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
               WHEN e.established_at IS NOT NULL AND e.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
               WHEN e.focused_at IS NOT NULL AND e.focused_at<@StatusAtExclusive THEN 'FOCUS'
               WHEN e.observed_at IS NOT NULL AND e.observed_at<@StatusAtExclusive THEN 'OBSERVING'
               ELSE 'DISCOVERED'
           END stage_at_end
    FROM pair_trend_live_event_fixture e
    WHERE e.algorithm_version=@AlgorithmVersion
      AND e.root_5m_eob IS NOT NULL
      AND e.root_5m_eob>=@DateFrom
      AND e.root_5m_eob<@DateToExclusive
), grouped AS (
    SELECT symbol,
           MAX(root_5m_eob) latest_pivot_at,
           MAX(CASE WHEN pivot_type='TOP' THEN root_5m_eob END) latest_top_at,
           MAX(CASE WHEN pivot_type='BOTTOM' THEN root_5m_eob END) latest_bottom_at,
           COUNT(*) event_count,
           SUM(pivot_type='TOP') top_count,
           SUM(pivot_type='BOTTOM') bottom_count,
           SUM(stage_at_end<>'INVALIDATED') active_at_end_count,
           SUM(stage_at_end='INVALIDATED') invalidated_at_end_count
    FROM filtered
    GROUP BY symbol
)
SELECT grouped.symbol,latest.symbol_name,
       grouped.latest_pivot_at,grouped.latest_top_at,grouped.latest_bottom_at,
       latest.latest_stage_at_end,
       grouped.event_count,grouped.top_count,grouped.bottom_count,
       grouped.active_at_end_count,grouped.invalidated_at_end_count
FROM grouped
JOIN LATERAL (
    SELECT latest_candidate.symbol_name,
           CASE
               WHEN latest_candidate.invalidated_at IS NOT NULL
                    AND latest_candidate.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
               WHEN latest_candidate.established_at IS NOT NULL
                    AND latest_candidate.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
               WHEN latest_candidate.focused_at IS NOT NULL
                    AND latest_candidate.focused_at<@StatusAtExclusive THEN 'FOCUS'
               WHEN latest_candidate.observed_at IS NOT NULL
                    AND latest_candidate.observed_at<@StatusAtExclusive THEN 'OBSERVING'
               ELSE 'DISCOVERED'
           END latest_stage_at_end
    FROM pair_trend_live_event_fixture_latest latest_candidate
    WHERE latest_candidate.algorithm_version=@AlgorithmVersion
      AND latest_candidate.root_5m_eob IS NOT NULL
      AND latest_candidate.root_5m_eob>=@DateFrom
      AND latest_candidate.root_5m_eob<@DateToExclusive
      AND latest_candidate.symbol=grouped.symbol
    ORDER BY latest_candidate.root_5m_eob DESC,latest_candidate.id DESC
    LIMIT 1
) AS latest ON TRUE;

CREATE TEMPORARY TABLE phase1_failures (
    check_name VARCHAR(96) NOT NULL,
    details VARCHAR(512) NOT NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

-- Read each physical TEMPORARY table only once per statement while proving that
-- the lookup clone contains the same row count and id/content_hash fingerprint.
SELECT COUNT(*),
       COALESCE(BIT_XOR(CAST(CONV(SUBSTRING(
           SHA2(CONCAT(id,':',content_hash),256),1,16),16,10) AS UNSIGNED)),0)
INTO @FixtureRowCount,@FixtureContentHash
FROM pair_trend_live_event_fixture;

SELECT COUNT(*),
       COALESCE(BIT_XOR(CAST(CONV(SUBSTRING(
           SHA2(CONCAT(id,':',content_hash),256),1,16),16,10) AS UNSIGNED)),0)
INTO @LatestFixtureRowCount,@LatestFixtureContentHash
FROM pair_trend_live_event_fixture_latest;

INSERT INTO phase1_failures(check_name,details)
SELECT 'fixture_clone_parity',
       CONCAT('main/latest count=',@FixtureRowCount,'/',@LatestFixtureRowCount,
              ', hash=',@FixtureContentHash,'/',@LatestFixtureContentHash)
WHERE @FixtureRowCount<>@LatestFixtureRowCount
   OR NOT (@FixtureContentHash<=>@LatestFixtureContentHash);

-- Old/new parity plus independent expected values prevent two identically wrong
-- queries from making the fixture pass.
INSERT INTO phase1_failures(check_name,details)
SELECT 'legacy_vs_phase1',CONCAT('different result for ',legacy.symbol)
FROM phase1_legacy_result legacy
LEFT JOIN phase1_optimized_result optimized ON optimized.symbol=legacy.symbol
WHERE optimized.symbol IS NULL
   OR NOT (legacy.symbol_name <=> optimized.symbol_name)
   OR NOT (legacy.latest_pivot_at <=> optimized.latest_pivot_at)
   OR NOT (legacy.latest_top_at <=> optimized.latest_top_at)
   OR NOT (legacy.latest_bottom_at <=> optimized.latest_bottom_at)
   OR NOT (legacy.latest_stage_at_end <=> optimized.latest_stage_at_end)
   OR legacy.event_count<>optimized.event_count
   OR legacy.top_count<>optimized.top_count
   OR legacy.bottom_count<>optimized.bottom_count
   OR legacy.active_at_end_count<>optimized.active_at_end_count
   OR legacy.invalidated_at_end_count<>optimized.invalidated_at_end_count;

INSERT INTO phase1_failures(check_name,details)
SELECT 'phase1_vs_legacy',CONCAT('unexpected optimized result for ',optimized.symbol)
FROM phase1_optimized_result optimized
LEFT JOIN phase1_legacy_result legacy ON legacy.symbol=optimized.symbol
WHERE legacy.symbol IS NULL;

INSERT INTO phase1_failures(check_name,details)
SELECT 'independent_group_total',CONCAT('expected 25 groups, got ',COUNT(*))
FROM phase1_optimized_result
HAVING COUNT(*)<>25;

INSERT INTO phase1_failures(check_name,details)
SELECT 'independent_event_totals',
       CONCAT('expected events/top/bottom=29/15/14, got ',
              SUM(event_count),'/',SUM(top_count),'/',SUM(bottom_count))
FROM phase1_optimized_result
HAVING SUM(event_count)<>29 OR SUM(top_count)<>15 OR SUM(bottom_count)<>14;

INSERT INTO phase1_failures(check_name,details)
SELECT 'same_root_id_tiebreak','id DESC did not select final name/stage'
WHERE NOT EXISTS (
    SELECT 1 FROM phase1_optimized_result
    WHERE symbol='SHSE.600001'
      AND symbol_name='TieLatest'
      AND latest_pivot_at='2026-08-17 14:00:00.000000'
      AND latest_stage_at_end='ESTABLISHED'
      AND event_count=3
);

INSERT INTO phase1_failures(check_name,details)
SELECT 'current_stage_independent','stageAtEnd must not be replaced with current stage/is_active'
WHERE NOT EXISTS (
    SELECT 1
    FROM pair_trend_live_event_fixture fixture
    JOIN phase1_optimized_result grouped ON grouped.symbol=fixture.symbol
    WHERE fixture.id=2002
      AND fixture.stage='INVALIDATED'
      AND fixture.is_active=FALSE
      AND grouped.latest_stage_at_end='ESTABLISHED'
);

-- Page size 20: page 1 has 20 groups, page 2 has 5, and page 3 is an
-- out-of-range metadata sentinel that still carries total=25.
CREATE TEMPORARY TABLE phase1_page_result (
    page_no INT NOT NULL,
    total_groups BIGINT NOT NULL,
    symbol VARCHAR(32) NULL,
    latest_pivot_at DATETIME(6) NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

-- Capture metadata in a separate statement. Each page INSERT then scans the
-- optimized TEMPORARY result exactly once, including the empty-page sentinel.
SELECT COUNT(DISTINCT symbol) INTO @GroupTotal
FROM phase1_optimized_result;

INSERT INTO phase1_page_result
SELECT 1,@GroupTotal,paged.symbol,paged.latest_pivot_at
FROM (SELECT 1 AS anchor) metadata
LEFT JOIN (
    SELECT symbol,latest_pivot_at FROM phase1_optimized_result
    ORDER BY latest_pivot_at DESC,symbol ASC LIMIT 20 OFFSET 0
) paged ON TRUE;

INSERT INTO phase1_page_result
SELECT 2,@GroupTotal,paged.symbol,paged.latest_pivot_at
FROM (SELECT 1 AS anchor) metadata
LEFT JOIN (
    SELECT symbol,latest_pivot_at FROM phase1_optimized_result
    ORDER BY latest_pivot_at DESC,symbol ASC LIMIT 20 OFFSET 20
) paged ON TRUE;

INSERT INTO phase1_page_result
SELECT 3,@GroupTotal,paged.symbol,paged.latest_pivot_at
FROM (SELECT 1 AS anchor) metadata
LEFT JOIN (
    SELECT symbol,latest_pivot_at FROM phase1_optimized_result
    ORDER BY latest_pivot_at DESC,symbol ASC LIMIT 20 OFFSET 40
) paged ON TRUE;

INSERT INTO phase1_failures(check_name,details)
SELECT 'pagination_counts',
       CONCAT('expected page rows 20/5/0, got ',
              SUM(page_no=1 AND symbol IS NOT NULL),'/',
              SUM(page_no=2 AND symbol IS NOT NULL),'/',
              SUM(page_no=3 AND symbol IS NOT NULL))
FROM phase1_page_result
HAVING SUM(page_no=1 AND symbol IS NOT NULL)<>20
    OR SUM(page_no=2 AND symbol IS NOT NULL)<>5
    OR SUM(page_no=3 AND symbol IS NOT NULL)<>0;

INSERT INTO phase1_failures(check_name,details)
SELECT 'pagination_true_total','every page, including empty page 3, must carry total=25'
FROM phase1_page_result
HAVING MIN(total_groups)<>25 OR MAX(total_groups)<>25
    OR SUM(page_no=3 AND symbol IS NULL)<>1;

SELECT COUNT(symbol),COUNT(DISTINCT symbol)
INTO @PagedSymbolCount,@PagedDistinctSymbolCount
FROM phase1_page_result;

INSERT INTO phase1_failures(check_name,details)
SELECT 'pagination_overlap',
       CONCAT('page symbols/distinct=',@PagedSymbolCount,'/',@PagedDistinctSymbolCount)
WHERE @PagedSymbolCount<>@PagedDistinctSymbolCount;

-- True-empty range: unlike an overflow page, total must be zero.
CREATE TEMPORARY TABLE phase1_true_empty_result (
    total_groups BIGINT NOT NULL,
    symbol VARCHAR(32) NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

-- Do not model metadata and page as two consumers of one grouped CTE here.
-- MySQL may inline both consumers and reopen the TEMPORARY fixture. Split total
-- and page into independent single-scan statements; this test verifies response
-- semantics, while production EXPLAIN verifies the real-table execution shape.
SELECT COUNT(DISTINCT e.symbol) INTO @TrueEmptyTotal
FROM pair_trend_live_event_fixture e
WHERE e.algorithm_version=@AlgorithmVersion
  AND e.root_5m_eob>='2026-08-16 00:00:00.000000'
  AND e.root_5m_eob<'2026-08-17 00:00:00.000000';

INSERT INTO phase1_true_empty_result
SELECT @TrueEmptyTotal,paged.symbol
FROM (SELECT 1 AS anchor) metadata
LEFT JOIN (
    SELECT e.symbol,MAX(e.root_5m_eob) latest_pivot_at
    FROM pair_trend_live_event_fixture e
    WHERE e.algorithm_version=@AlgorithmVersion
      AND e.root_5m_eob>='2026-08-16 00:00:00.000000'
      AND e.root_5m_eob<'2026-08-17 00:00:00.000000'
    GROUP BY e.symbol
    ORDER BY latest_pivot_at DESC,e.symbol ASC
    LIMIT 20 OFFSET 0
) paged ON TRUE;

INSERT INTO phase1_failures(check_name,details)
SELECT 'true_empty','empty range must return one total=0 metadata sentinel'
WHERE NOT EXISTS (
    SELECT 1 FROM phase1_true_empty_result
    HAVING COUNT(*)=1 AND MIN(total_groups)=0 AND SUM(symbol IS NOT NULL)=0
);

-- Historical-name keyword query. The same keyword predicate is intentionally
-- present in both aggregation and latest-row lookup.
CREATE TEMPORARY TABLE phase1_keyword_result (
    symbol VARCHAR(32) NOT NULL,
    symbol_name VARCHAR(128) NULL,
    latest_pivot_at DATETIME(6) NOT NULL,
    event_count BIGINT NOT NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

SET @Keyword = 'HistoricalOld';
INSERT INTO phase1_keyword_result
WITH filtered AS (
    SELECT e.symbol,e.root_5m_eob
    FROM pair_trend_live_event_fixture e
    WHERE e.algorithm_version=@AlgorithmVersion
      AND e.root_5m_eob>=@DateFrom AND e.root_5m_eob<@DateToExclusive
      AND (INSTR(UPPER(e.symbol),@Keyword)>0
           OR INSTR(UPPER(COALESCE(e.symbol_name,'')),@Keyword)>0)
), grouped AS (
    SELECT symbol,MAX(root_5m_eob) latest_pivot_at,COUNT(*) event_count
    FROM filtered GROUP BY symbol
)
SELECT grouped.symbol,latest.symbol_name,grouped.latest_pivot_at,grouped.event_count
FROM grouped
JOIN LATERAL (
    SELECT latest_candidate.symbol_name
    FROM pair_trend_live_event_fixture_latest latest_candidate
    WHERE latest_candidate.algorithm_version=@AlgorithmVersion
      AND latest_candidate.root_5m_eob>=@DateFrom
      AND latest_candidate.root_5m_eob<@DateToExclusive
      AND (INSTR(UPPER(latest_candidate.symbol),@Keyword)>0
           OR INSTR(UPPER(COALESCE(latest_candidate.symbol_name,'')),@Keyword)>0)
      AND latest_candidate.symbol=grouped.symbol
    ORDER BY latest_candidate.root_5m_eob DESC,latest_candidate.id DESC
    LIMIT 1
) AS latest ON TRUE;

INSERT INTO phase1_failures(check_name,details)
SELECT 'historical_name_keyword','keyword lookup escaped into unfiltered current name'
WHERE NOT EXISTS (
    SELECT 1 FROM phase1_keyword_result
    HAVING COUNT(*)=1
       AND MIN(symbol)='SHSE.600002'
       AND MIN(symbol_name)='HistoricalOldName'
       AND MIN(latest_pivot_at)='2026-08-17 10:00:00.000000'
       AND MIN(event_count)=1
);

-- Independent stage expectations cover transitions immediately before, exactly
-- equal to, and immediately after the DATETIME(6) exclusive cutoff.
CREATE TEMPORARY TABLE phase1_stage_expected (
    event_id BIGINT NOT NULL PRIMARY KEY,
    expected_stage_at_end VARCHAR(24) NOT NULL,
    expected_current_stage VARCHAR(24) NOT NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

INSERT INTO phase1_stage_expected VALUES
    (1004,'OBSERVING','OBSERVING'),
    (1005,'FOCUS','FOCUS'),
    (1006,'ESTABLISHED','ESTABLISHED'),
    (1007,'INVALIDATED','INVALIDATED'),
    (1008,'FOCUS','INVALIDATED'),
    (1009,'ESTABLISHED','INVALIDATED'),
    (1010,'OBSERVING','INVALIDATED'),
    (1011,'DISCOVERED','INVALIDATED'),
    (2002,'ESTABLISHED','INVALIDATED');

INSERT INTO phase1_failures(check_name,details)
SELECT 'datetime6_stage_boundary',
       CONCAT('id=',expected.event_id,', expected=',expected.expected_stage_at_end,
              ', actual=',
              CASE
                  WHEN fixture.invalidated_at IS NOT NULL AND fixture.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
                  WHEN fixture.established_at IS NOT NULL AND fixture.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
                  WHEN fixture.focused_at IS NOT NULL AND fixture.focused_at<@StatusAtExclusive THEN 'FOCUS'
                  WHEN fixture.observed_at IS NOT NULL AND fixture.observed_at<@StatusAtExclusive THEN 'OBSERVING'
                  ELSE 'DISCOVERED'
              END)
FROM phase1_stage_expected expected
JOIN pair_trend_live_event_fixture fixture ON fixture.id=expected.event_id
WHERE expected.expected_stage_at_end<>
      CASE
          WHEN fixture.invalidated_at IS NOT NULL AND fixture.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
          WHEN fixture.established_at IS NOT NULL AND fixture.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
          WHEN fixture.focused_at IS NOT NULL AND fixture.focused_at<@StatusAtExclusive THEN 'FOCUS'
          WHEN fixture.observed_at IS NOT NULL AND fixture.observed_at<@StatusAtExclusive THEN 'OBSERVING'
          ELSE 'DISCOVERED'
      END
   OR expected.expected_current_stage<>fixture.stage;

-- Materialize the correctly computed event state once. The previous independent
-- table has already proved the CASE expression, so filter assertions below can be
-- concise and give exact event/group counts.
CREATE TEMPORARY TABLE phase1_evaluated (
    id BIGINT NOT NULL PRIMARY KEY,
    symbol VARCHAR(32) NOT NULL,
    pivot_type VARCHAR(16) NOT NULL,
    frequencies VARCHAR(64) NOT NULL,
    stage_at_end VARCHAR(24) NOT NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

INSERT INTO phase1_evaluated
SELECT e.id,e.symbol,e.pivot_type,e.frequencies,
       CASE
           WHEN e.invalidated_at IS NOT NULL AND e.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
           WHEN e.established_at IS NOT NULL AND e.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
           WHEN e.focused_at IS NOT NULL AND e.focused_at<@StatusAtExclusive THEN 'FOCUS'
           WHEN e.observed_at IS NOT NULL AND e.observed_at<@StatusAtExclusive THEN 'OBSERVING'
           ELSE 'DISCOVERED'
       END
FROM pair_trend_live_event_fixture e
WHERE e.algorithm_version=@AlgorithmVersion
  AND e.root_5m_eob>=@DateFrom AND e.root_5m_eob<@DateToExclusive;

CREATE TEMPORARY TABLE phase1_filter_result (
    case_name VARCHAR(96) NOT NULL PRIMARY KEY,
    event_count BIGINT NOT NULL,
    group_count BIGINT NOT NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

INSERT INTO phase1_filter_result
SELECT 'includeInvalidated=true',COUNT(*),COUNT(DISTINCT symbol) FROM phase1_evaluated;
INSERT INTO phase1_filter_result
SELECT 'includeInvalidated=false',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE stage_at_end<>'INVALIDATED';
INSERT INTO phase1_filter_result
SELECT 'activeAtEnd=true',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE stage_at_end<>'INVALIDATED';
INSERT INTO phase1_filter_result
SELECT 'activeAtEnd=false',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE stage_at_end='INVALIDATED';
-- activeAtEnd is authoritative when non-null, so includeInvalidated=false does
-- not append a second, contradictory predicate.
INSERT INTO phase1_filter_result
SELECT 'include=false+active=false',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE stage_at_end='INVALIDATED';
INSERT INTO phase1_filter_result
SELECT 'stage=INVALIDATED+include=false',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated
WHERE stage_at_end='INVALIDATED' AND stage_at_end<>'INVALIDATED';
INSERT INTO phase1_filter_result
SELECT 'stage=INVALIDATED+active=true',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated
WHERE stage_at_end='INVALIDATED' AND stage_at_end<>'INVALIDATED';
INSERT INTO phase1_filter_result
SELECT 'stage=FOCUS+active=false',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated
WHERE stage_at_end='FOCUS' AND stage_at_end='INVALIDATED';
INSERT INTO phase1_filter_result
SELECT 'pivot=TOP',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE pivot_type='TOP';
INSERT INTO phase1_filter_result
SELECT 'pivot=BOTTOM',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE pivot_type='BOTTOM';
INSERT INTO phase1_filter_result
SELECT 'frequency=5m',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE FIND_IN_SET('5m',frequencies)>0;
INSERT INTO phase1_filter_result
SELECT 'frequency=30m',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE FIND_IN_SET('30m',frequencies)>0;
INSERT INTO phase1_filter_result
SELECT 'frequency=60m',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE FIND_IN_SET('60m',frequencies)>0;
INSERT INTO phase1_filter_result
SELECT 'frequency=1d',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE FIND_IN_SET('1d',frequencies)>0;
INSERT INTO phase1_filter_result
SELECT 'pivot=TOP+frequency=1d',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE pivot_type='TOP' AND FIND_IN_SET('1d',frequencies)>0;
INSERT INTO phase1_filter_result
SELECT 'pivot=BOTTOM+frequency=1d',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE pivot_type='BOTTOM' AND FIND_IN_SET('1d',frequencies)>0;
INSERT INTO phase1_filter_result
SELECT 'stage=FOCUS+active=true',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE stage_at_end='FOCUS' AND stage_at_end<>'INVALIDATED';
INSERT INTO phase1_filter_result
SELECT 'stage=INVALIDATED+active=false',COUNT(*),COUNT(DISTINCT symbol)
FROM phase1_evaluated WHERE stage_at_end='INVALIDATED';

CREATE TEMPORARY TABLE phase1_filter_expected LIKE phase1_filter_result;
INSERT INTO phase1_filter_expected VALUES
    ('includeInvalidated=true',29,25),
    ('includeInvalidated=false',28,24),
    ('activeAtEnd=true',28,24),
    ('activeAtEnd=false',1,1),
    ('include=false+active=false',1,1),
    ('stage=INVALIDATED+include=false',0,0),
    ('stage=INVALIDATED+active=true',0,0),
    ('stage=FOCUS+active=false',0,0),
    ('pivot=TOP',15,13),
    ('pivot=BOTTOM',14,12),
    ('frequency=5m',26,24),
    ('frequency=30m',2,2),
    ('frequency=60m',2,2),
    ('frequency=1d',1,1),
    ('pivot=TOP+frequency=1d',0,0),
    ('pivot=BOTTOM+frequency=1d',1,1),
    ('stage=FOCUS+active=true',4,4),
    ('stage=INVALIDATED+active=false',1,1);

INSERT INTO phase1_failures(check_name,details)
SELECT 'filter_matrix',
       CONCAT(expected.case_name,': expected ',expected.event_count,'/',expected.group_count,
              ', got ',COALESCE(actual.event_count,-1),'/',COALESCE(actual.group_count,-1))
FROM phase1_filter_expected expected
LEFT JOIN phase1_filter_result actual ON actual.case_name=expected.case_name
WHERE actual.case_name IS NULL
   OR actual.event_count<>expected.event_count
   OR actual.group_count<>expected.group_count;

INSERT INTO phase1_failures(check_name,details)
SELECT 'filter_matrix_unexpected',CONCAT('unexpected case ',actual.case_name)
FROM phase1_filter_result actual
LEFT JOIN phase1_filter_expected expected ON expected.case_name=actual.case_name
WHERE expected.case_name IS NULL;

-- Print every failure before enforcing the final assertion. CHECK is used rather
-- than a stored procedure/SIGNAL so this script never creates a persistent object.
SELECT check_name,details FROM phase1_failures ORDER BY check_name,details;

CREATE TEMPORARY TABLE phase1_assertion_guard (
    verdict VARCHAR(8) NOT NULL,
    CONSTRAINT chk_phase1_fixture_pass CHECK (verdict='PASS')
) ENGINE=InnoDB DEFAULT CHARACTER SET latin1 COLLATE latin1_swedish_ci;

INSERT INTO phase1_assertion_guard(verdict)
SELECT IF(COUNT(*)=0,'PASS','FAIL') FROM phase1_failures;

-- Final output also obeys the single-open rule: collect multiple aggregates in
-- one scan, then render only session variables.
SELECT COUNT(*),COALESCE(SUM(event_count),0)
INTO @PassStockGroups,@PassMatchingEvents
FROM phase1_optimized_result;

SELECT COUNT(*) INTO @PassFilterCases
FROM phase1_filter_result;

SELECT
    'PASS: pair-trend grouped-query phase 1 semantics verified' AS result,
    @PassStockGroups AS stock_groups,
    @PassMatchingEvents AS matching_events,
    @PassFilterCases AS filter_cases,
    'temporary fixture only; disconnect removes all test state' AS cleanup;
