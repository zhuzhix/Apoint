#!/usr/bin/env bash
set -euo pipefail

container_name="${MYSQL_CONTAINER_NAME:-astock-cloud-mysql}"
database_name="${MYSQL_DATABASE_NAME:-astock_monitor}"
action="${1:-}"

mysql_exec() {
  docker exec "${container_name}" sh -lc \
    'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --batch --skip-column-names "$2" -e "$1"' -- "$1" "${database_name}"
}

case "${action}" in
  drop-5m-pre-aug-2026)
    targets="'p202601','p202602','p202603','p202604','p202605','p202606','p202607'"
    target_count="$(mysql_exec "SELECT COUNT(*) FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='kline_bar_5m' AND PARTITION_NAME IN (${targets});")"
    keep_count="$(mysql_exec "SELECT COUNT(*) FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='kline_bar_5m' AND PARTITION_NAME='p202608';")"
    [[ "${target_count}" == "7" ]] || { echo "Expected 7 removable partitions, found ${target_count}." >&2; exit 1; }
    [[ "${keep_count}" == "1" ]] || { echo "August keep partition p202608 is missing." >&2; exit 1; }
    mysql_exec "SELECT PARTITION_NAME, PARTITION_DESCRIPTION, TABLE_ROWS FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='kline_bar_5m' AND PARTITION_NAME IN (${targets},'p202608') ORDER BY PARTITION_ORDINAL_POSITION;"
    mysql_exec "ALTER TABLE kline_bar_5m DROP PARTITION p202601,p202602,p202603,p202604,p202605,p202606,p202607;"
    mysql_exec "SELECT PARTITION_NAME, PARTITION_DESCRIPTION, TABLE_ROWS FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='kline_bar_5m' ORDER BY PARTITION_ORDINAL_POSITION LIMIT 3;"
    ;;
  preview-pair-pre-july-2026)
    mysql_exec "SELECT 'events_to_delete', COUNT(*) FROM pair_trend_event WHERE last_seen_at < '2026-07-01';
      SELECT 'hits_to_delete', COUNT(*) FROM pair_trend_hit h INNER JOIN pair_trend_event e ON e.id=h.event_id WHERE e.last_seen_at < '2026-07-01';
      SELECT 'lifecycle_to_delete', COUNT(*) FROM pair_trend_lifecycle l INNER JOIN pair_trend_event e ON e.id=l.event_id WHERE e.last_seen_at < '2026-07-01';
      SELECT 'cross_cutoff_events_kept', COUNT(*) FROM pair_trend_event WHERE first_seen_at < '2026-07-01' AND last_seen_at >= '2026-07-01';"
    ;;
  *)
    echo "Usage: $0 [drop-5m-pre-aug-2026|preview-pair-pre-july-2026]" >&2
    exit 2
    ;;
esac
