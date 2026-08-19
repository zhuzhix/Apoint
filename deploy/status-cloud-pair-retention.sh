#!/usr/bin/env bash
set -euo pipefail

base_dir="${ASTOCK_DATA_DIR:-/opt/astock-data}"
container_name="${MYSQL_CONTAINER_NAME:-astock-cloud-mysql}"
cutoff_date="${1:-2026-07-01}"
pid_file="${base_dir}/logs/pair-retention-${cutoff_date}.pid"

if [[ -f "${pid_file}" ]]; then
  retention_pid="$(cat "${pid_file}")"
  if kill -0 "${retention_pid}" 2>/dev/null; then
    echo "runner=running pid=${retention_pid}"
  else
    echo "runner=finished pid=${retention_pid}"
  fi
else
  echo "runner=unknown"
fi

docker exec "${container_name}" sh -lc \
  'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --batch --skip-column-names astock_monitor -e "
    SELECT id,cutoff_date,status,planned_event_count,planned_hit_count,
           planned_lifecycle_count,deleted_event_count,last_event_id,max_event_id,
           started_at,finished_at,COALESCE(error_message,\"\")
    FROM pair_trend_retention_run
    ORDER BY id DESC LIMIT 1;
    SELECT ID,COMMAND,TIME,COALESCE(STATE,\"\"),LEFT(COALESCE(INFO,\"\"),160)
    FROM information_schema.PROCESSLIST
    WHERE COMMAND<>\"Sleep\"
    ORDER BY TIME DESC LIMIT 10;
  "'
