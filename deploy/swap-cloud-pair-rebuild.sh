#!/usr/bin/env bash
set -euo pipefail

base_dir="${ASTOCK_DATA_DIR:-/opt/astock-data}"
container_name="${MYSQL_CONTAINER_NAME:-astock-cloud-mysql}"
sql_file="${base_dir}/swap-cloud-pair-rebuild.sql"

[[ -f "${sql_file}" ]] || { echo "SQL file not found: ${sql_file}" >&2; exit 1; }
docker exec -i "${container_name}" sh -lc \
  'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --binary-mode=1 astock_monitor' \
  < "${sql_file}"
