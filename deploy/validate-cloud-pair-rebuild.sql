USE astock_monitor;

SELECT table_name
FROM information_schema.tables
WHERE table_schema=DATABASE()
  AND table_name IN (
      'pair_trend_event','pair_trend_hit','pair_trend_lifecycle',
      'pair_trend_event_old_20260815','pair_trend_hit_old_20260815','pair_trend_lifecycle_old_20260815'
  )
ORDER BY table_name;

SELECT 'final_counts' AS check_name,
       (SELECT COUNT(*) FROM pair_trend_event) AS event_count,
       (SELECT COUNT(*) FROM pair_trend_hit) AS hit_count,
       (SELECT COUNT(*) FROM pair_trend_lifecycle) AS lifecycle_count;

SELECT 'final_orphans' AS check_name,
       (SELECT COUNT(*) FROM pair_trend_hit h
        LEFT JOIN pair_trend_event e ON e.id=h.event_id
        WHERE e.id IS NULL) AS hit_orphans,
       (SELECT COUNT(*) FROM pair_trend_lifecycle l
        LEFT JOIN pair_trend_event e ON e.id=l.event_id
        WHERE e.id IS NULL) AS lifecycle_orphans;

SELECT TABLE_NAME,CONSTRAINT_NAME,REFERENCED_TABLE_NAME
FROM information_schema.REFERENTIAL_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA=DATABASE()
  AND TABLE_NAME IN ('pair_trend_event','pair_trend_hit','pair_trend_lifecycle')
ORDER BY TABLE_NAME,CONSTRAINT_NAME;

SELECT @@GLOBAL.log_bin,@@GLOBAL.innodb_flush_log_at_trx_commit,@@GLOBAL.innodb_change_buffering,@@GLOBAL.event_scheduler;

SELECT table_name,
       ROUND((data_length+index_length)/1024/1024/1024,2) AS size_gb
FROM information_schema.tables
WHERE table_schema=DATABASE()
  AND table_name IN ('pair_trend_event','pair_trend_hit','pair_trend_lifecycle',
                     'pair_trend_event_old_20260815','pair_trend_hit_old_20260815','pair_trend_lifecycle_old_20260815')
ORDER BY table_name;
