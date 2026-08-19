#!/usr/bin/env bash
set -euo pipefail

container_name="${MYSQL_CONTAINER_NAME:-astock-cloud-mysql}"
action="${1:-list}"

mysql_exec() {
  docker exec "${container_name}" sh -lc \
    'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --batch --skip-column-names -e "$1"' -- "$1"
}

case "${action}" in
  list)
    mysql_exec "SHOW BINARY LOGS;"
    ;;
  status)
    mysql_exec "SELECT ID, USER, COMMAND, TIME, COALESCE(STATE, ''), LEFT(COALESCE(INFO, ''), 160) FROM information_schema.PROCESSLIST WHERE COMMAND <> 'Sleep' ORDER BY TIME DESC;"
    mysql_exec "SELECT trx_id, TIMESTAMPDIFF(SECOND, trx_started, NOW()), trx_state, trx_rows_modified, trx_rows_locked FROM information_schema.INNODB_TRX ORDER BY trx_started;"
    ;;
  migration-fast)
    mysql_exec "SET GLOBAL innodb_flush_log_at_trx_commit = 0; SET GLOBAL sync_binlog = 0;"
    mysql_exec "SELECT @@GLOBAL.innodb_flush_log_at_trx_commit, @@GLOBAL.sync_binlog;"
    ;;
  restore-safe)
    mysql_exec "SET GLOBAL innodb_flush_log_at_trx_commit = 2; SET GLOBAL sync_binlog = 1; SET GLOBAL innodb_change_buffering = 'none';"
    mysql_exec "SELECT @@GLOBAL.innodb_flush_log_at_trx_commit, @@GLOBAL.sync_binlog, @@GLOBAL.innodb_change_buffering;"
    ;;
  tuning-status)
    mysql_exec "SELECT @@GLOBAL.innodb_change_buffering, @@GLOBAL.innodb_io_capacity, @@GLOBAL.innodb_io_capacity_max, @@GLOBAL.innodb_buffer_pool_size;"
    ;;
  migration-io-fast)
    mysql_exec "SET GLOBAL innodb_change_buffering = 'all';"
    mysql_exec "SELECT @@GLOBAL.innodb_change_buffering, @@GLOBAL.innodb_io_capacity, @@GLOBAL.innodb_io_capacity_max;"
    ;;
  validate-counts)
    mysql_exec "SELECT 'kline_bar_5m', COUNT(*), 0 FROM astock_monitor.kline_bar_5m
      UNION ALL SELECT 'kline_bar_agg', COUNT(*), 0 FROM astock_monitor.kline_bar_agg
      UNION ALL SELECT 'kline_bar_daily', COUNT(*), 0 FROM astock_monitor.kline_bar_daily
      UNION ALL SELECT 'pair_trend_event', COUNT(*), COALESCE(MAX(id),0) FROM astock_monitor.pair_trend_event
      UNION ALL SELECT 'pair_trend_hit', COUNT(*), COALESCE(MAX(id),0) FROM astock_monitor.pair_trend_hit
      UNION ALL SELECT 'pair_trend_lifecycle', COUNT(*), COALESCE(MAX(id),0) FROM astock_monitor.pair_trend_lifecycle
      UNION ALL SELECT 'pair_trend_live_event', COUNT(*), COALESCE(MAX(id),0) FROM astock_monitor.pair_trend_live_event
      UNION ALL SELECT 'pair_trend_live_hit', COUNT(*), COALESCE(MAX(id),0) FROM astock_monitor.pair_trend_live_hit
      UNION ALL SELECT 'pair_trend_live_lifecycle', COUNT(*), COALESCE(MAX(id),0) FROM astock_monitor.pair_trend_live_lifecycle;"
    mysql_exec "SELECT 'kline_bar_5m', MIN(trading_date), MAX(trading_date), MAX(eob) FROM astock_monitor.kline_bar_5m
      UNION ALL SELECT 'kline_bar_agg', MIN(trading_date), MAX(trading_date), MAX(eob) FROM astock_monitor.kline_bar_agg
      UNION ALL SELECT 'kline_bar_daily', MIN(trading_date), MAX(trading_date), MAX(eob) FROM astock_monitor.kline_bar_daily;"
    ;;
  purge-before-active)
    active_log="$(mysql_exec "SHOW BINARY LOG STATUS;" | awk 'NR == 1 { print $1 }')"
    if [[ -z "${active_log}" ]]; then
      echo "No active binary log found; nothing purged."
      exit 0
    fi
    mysql_exec "PURGE BINARY LOGS TO '${active_log}';"
    echo "Purged sealed binary logs before ${active_log}; active log retained."
    ;;
  kill-query)
    query_id="${2:-}"
    [[ "${query_id}" =~ ^[0-9]+$ ]] || {
      echo "A numeric MySQL connection id is required." >&2
      exit 2
    }
    mysql_exec "KILL ${query_id};"
    echo "Killed MySQL connection ${query_id}."
    ;;
  fail-retention-run)
    retention_run_id="${2:-}"
    [[ "${retention_run_id}" =~ ^[0-9]+$ ]] || {
      echo "A numeric retention run id is required." >&2
      exit 2
    }
    mysql_exec "UPDATE astock_monitor.pair_trend_retention_run
      SET status='failed',
          error_message='Stopped to switch from cascade deletion to direct child-table batches',
          finished_at=UTC_TIMESTAMP(6)
      WHERE id=${retention_run_id} AND status='running';"
    echo "Marked stale retention run ${retention_run_id} as failed."
    ;;
  *)
    echo "Usage: $0 [list|status|migration-fast|restore-safe|tuning-status|migration-io-fast|validate-counts|purge-before-active|kill-query ID|fail-retention-run ID]" >&2
    exit 2
    ;;
esac
