#!/usr/bin/env bash
set -euo pipefail

base_dir="${ASTOCK_DATA_DIR:-/opt/astock-data}"
container_name="${MYSQL_CONTAINER_NAME:-astock-cloud-mysql}"
sql_file="${base_dir}/rebuild-cloud-pair-trend.sql"
log_file="${base_dir}/logs/pair-rebuild-20260815.log"

[[ -f "${sql_file}" ]] || { echo "SQL file not found: ${sql_file}" >&2; exit 1; }
mkdir -p "${base_dir}/logs"

docker exec -i "${container_name}" sh -lc \
  'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --binary-mode=1 astock_monitor' \
  < "${sql_file}" > "${log_file}" 2>&1

cat "${log_file}"
