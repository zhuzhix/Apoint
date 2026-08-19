USE astock_monitor;

SET @cutoff_date = '2026-07-01';

-- 只保留截止日前仍有效的父事件；已被前一轮清理删除的旧事件自然不会进入副本。
DROP TABLE IF EXISTS pair_trend_lifecycle_compact_20260815;
DROP TABLE IF EXISTS pair_trend_hit_compact_20260815;
DROP TABLE IF EXISTS pair_trend_event_compact_20260815;

CREATE TABLE pair_trend_event_compact_20260815 LIKE pair_trend_event;
CREATE TABLE pair_trend_hit_compact_20260815 LIKE pair_trend_hit;
CREATE TABLE pair_trend_lifecycle_compact_20260815 LIKE pair_trend_lifecycle;

INSERT INTO pair_trend_event_compact_20260815
SELECT *
FROM pair_trend_event
WHERE last_seen_at >= @cutoff_date;

INSERT INTO pair_trend_hit_compact_20260815
SELECT h.*
FROM pair_trend_hit h
INNER JOIN pair_trend_event_compact_20260815 e ON e.id = h.event_id;

INSERT INTO pair_trend_lifecycle_compact_20260815
SELECT l.*
FROM pair_trend_lifecycle l
INNER JOIN pair_trend_event_compact_20260815 e ON e.id = l.event_id;

ALTER TABLE pair_trend_event_compact_20260815
    ADD CONSTRAINT fk_pair_trend_event_compact_run
        FOREIGN KEY (run_id) REFERENCES pair_trend_backtest_run(id)
        ON DELETE CASCADE;

ALTER TABLE pair_trend_hit_compact_20260815
    ADD CONSTRAINT fk_pair_trend_hit_compact_run
        FOREIGN KEY (run_id) REFERENCES pair_trend_backtest_run(id)
        ON DELETE CASCADE,
    ADD CONSTRAINT fk_pair_trend_hit_compact_event
        FOREIGN KEY (event_id) REFERENCES pair_trend_event_compact_20260815(id)
        ON DELETE CASCADE;

ALTER TABLE pair_trend_lifecycle_compact_20260815
    ADD CONSTRAINT fk_pair_trend_lifecycle_compact_run
        FOREIGN KEY (run_id) REFERENCES pair_trend_backtest_run(id)
        ON DELETE CASCADE,
    ADD CONSTRAINT fk_pair_trend_lifecycle_compact_event
        FOREIGN KEY (event_id) REFERENCES pair_trend_event_compact_20260815(id)
        ON DELETE CASCADE;

SELECT 'compact_counts' AS check_name,
       (SELECT COUNT(*) FROM pair_trend_event_compact_20260815) AS event_count,
       (SELECT COUNT(*) FROM pair_trend_hit_compact_20260815) AS hit_count,
       (SELECT COUNT(*) FROM pair_trend_lifecycle_compact_20260815) AS lifecycle_count;

SELECT 'compact_orphans' AS check_name,
       (SELECT COUNT(*)
        FROM pair_trend_hit_compact_20260815 h
        LEFT JOIN pair_trend_event_compact_20260815 e ON e.id = h.event_id
        WHERE e.id IS NULL) AS hit_orphans,
       (SELECT COUNT(*)
        FROM pair_trend_lifecycle_compact_20260815 l
        LEFT JOIN pair_trend_event_compact_20260815 e ON e.id = l.event_id
        WHERE e.id IS NULL) AS lifecycle_orphans;
