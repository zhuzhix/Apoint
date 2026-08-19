#!/usr/bin/env bash
set -euo pipefail

base_dir="${ASTOCK_DATA_DIR:-/opt/astock-data}"
container_name="${MYSQL_CONTAINER_NAME:-astock-cloud-mysql}"
migration_file="${base_dir}/024_pair_trend_retention.sql"

[[ -f "${migration_file}" ]] || {
  echo "Migration file not found: ${migration_file}" >&2
  exit 1
}

health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "${container_name}")"
[[ "${health}" == "healthy" ]] || {
  echo "MySQL is not healthy: ${health}" >&2
  exit 1
}

docker exec -i "${container_name}" sh -lc \
  'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --binary-mode=1 astock_monitor' \
  < "${migration_file}"

docker exec "${container_name}" sh -lc \
  'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --batch --skip-column-names astock_monitor -e "
    SELECT @@GLOBAL.log_bin,
           @@GLOBAL.innodb_flush_log_at_trx_commit,
           @@GLOBAL.innodb_change_buffering,
           @@GLOBAL.event_scheduler;
    SELECT COUNT(*) FROM information_schema.routines
      WHERE routine_schema=\"astock_monitor\"
        AND routine_name IN (\"run_pair_trend_retention\",\"run_pair_trend_retention_monthly\");
    SELECT event_name,status,event_definition
      FROM information_schema.events
      WHERE event_schema=\"astock_monitor\"
        AND event_name=\"ev_pair_trend_retention_monthly\";
    SELECT version,description FROM schema_migration WHERE version=\"024\";
  "'
