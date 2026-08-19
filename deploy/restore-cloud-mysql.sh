#!/usr/bin/env bash
set -euo pipefail

base_dir='/opt/astock-data'
migration_dir="${base_dir}/migration"
dump_file="${migration_dir}/astock-monitor-20260815.sql.zst"
status_file="${migration_dir}/mysql-import.status"
log_file="${migration_dir}/mysql-import.log"

cd "${base_dir}"

printf '%s  %s\n' \
  '89839fd3c6c572d835535b8b7998a280414df8c39f989e062f2ddc8c1820403f' \
  'migration/astock-monitor-20260815.sql.zst' |
  sha256sum --check --strict

docker compose --env-file .env stop redis >/dev/null 2>&1 || true
docker compose --env-file .env rm -f redis >/dev/null 2>&1 || true

docker exec astock-cloud-mysql sh -lc '
  mysql -uroot -p"$MYSQL_ROOT_PASSWORD" -e \
    "SET GLOBAL innodb_flush_log_at_trx_commit=2; SET GLOBAL sync_binlog=0;"
' >>"${log_file}" 2>&1

printf 'running\n' >"${status_file}"
printf '[%s] MySQL import started\n' "$(date --iso-8601=seconds)" \
  >>"${log_file}"

set +e
set -o pipefail
docker run --rm \
  -v "${migration_dir}:/backup:ro" \
  mysql:8.4 sh -lc \
  'zstd -dc /backup/astock-monitor-20260815.sql.zst' |
  docker exec -i astock-cloud-mysql sh -lc '
    mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --binary-mode=1
  ' >>"${log_file}" 2>&1
import_exit_code=$?
set -e

docker exec astock-cloud-mysql sh -lc '
  mysql -uroot -p"$MYSQL_ROOT_PASSWORD" -e \
    "SET GLOBAL innodb_flush_log_at_trx_commit=1; SET GLOBAL sync_binlog=1;"
' >>"${log_file}" 2>&1 || true

if [[ ${import_exit_code} -ne 0 ]]; then
  printf 'failed:%s\n' "${import_exit_code}" >"${status_file}"
  printf '[%s] MySQL import failed with code %s\n' \
    "$(date --iso-8601=seconds)" "${import_exit_code}" >>"${log_file}"
  exit "${import_exit_code}"
fi

printf 'complete\n' >"${status_file}"
printf '[%s] MySQL import completed\n' "$(date --iso-8601=seconds)" \
  >>"${log_file}"
