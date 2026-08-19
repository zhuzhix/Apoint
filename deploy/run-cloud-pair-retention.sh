#!/usr/bin/env bash
set -euo pipefail

base_dir="${ASTOCK_DATA_DIR:-/opt/astock-data}"
container_name="${MYSQL_CONTAINER_NAME:-astock-cloud-mysql}"
cutoff_date="${1:-2026-07-01}"
log_dir="${base_dir}/logs"
log_file="${log_dir}/pair-retention-${cutoff_date}.log"
pid_file="${log_dir}/pair-retention-${cutoff_date}.pid"

case "${cutoff_date}" in
  20??-??-??) ;;
  *) echo "Invalid cutoff date: ${cutoff_date}" >&2; exit 2 ;;
esac

mkdir -p "${log_dir}"

if [[ -f "${pid_file}" ]]; then
  old_pid="$(cat "${pid_file}")"
  if kill -0 "${old_pid}" 2>/dev/null; then
    echo "Retention is already running with PID ${old_pid}."
    exit 0
  fi
  rm -f "${pid_file}"
fi

nohup docker exec "${container_name}" sh -lc \
  'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --batch astock_monitor -e "CALL run_pair_trend_retention(\"'"${cutoff_date}"'\");"' \
  > "${log_file}" 2>&1 &
retention_pid=$!
echo "${retention_pid}" > "${pid_file}"

echo "Retention started: PID=${retention_pid} cutoff=${cutoff_date}"
echo "Log: ${log_file}"
