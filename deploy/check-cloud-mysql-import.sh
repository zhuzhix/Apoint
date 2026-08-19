#!/usr/bin/env bash
set -euo pipefail

status_file='/opt/astock-data/migration/mysql-import.status'
if [[ -f "${status_file}" ]]; then
  printf 'import_status='
  cat "${status_file}"
else
  printf 'import_status=not-started\n'
fi

docker exec astock-cloud-mysql sh -lc '
  mysql -uroot -p"$MYSQL_ROOT_PASSWORD" -N -e "
    SELECT CONCAT('"'"'schema_tables='"'"',COUNT(*))
    FROM information_schema.tables
    WHERE table_schema='"'"'astock_monitor'"'"';

    SELECT CONCAT('"'"'allocated_gb='"'"',
                  ROUND(COALESCE(SUM(data_length+index_length),0)/1024/1024/1024,2))
    FROM information_schema.tables
    WHERE table_schema='"'"'astock_monitor'"'"';

    SELECT CONCAT(table_name,'"'"'|'"'"',table_rows,'"'"'|'"'"',
                  ROUND((data_length+index_length)/1024/1024/1024,2))
    FROM information_schema.tables
    WHERE table_schema='"'"'astock_monitor'"'"'
    ORDER BY data_length+index_length DESC
    LIMIT 8;
  " 2>/dev/null
'

printf 'restore_processes='
pgrep -fc 'restore-cloud-mysql|zstd -dc|binary-mode=1' || true

zstd_pid="$(pgrep -f 'zstd -dc /backup/astock-monitor-20260815.sql.zst' |
  head -n 1 || true)"
if [[ -n "${zstd_pid}" ]]; then
  for descriptor in "/proc/${zstd_pid}/fd/"*; do
    target="$(readlink "${descriptor}" 2>/dev/null || true)"
    if [[ "${target}" == *astock-monitor-20260815.sql.zst ]]; then
      position="$(awk '/^pos:/{print $2}' \
        "/proc/${zstd_pid}/fdinfo/${descriptor##*/}")"
      total="$(stat -c '%s' \
        /opt/astock-data/migration/astock-monitor-20260815.sql.zst)"
      awk -v position="${position}" -v total="${total}" \
        'BEGIN { printf "compressed_read=%.1f%% (%d/%d bytes)\n",\
                        position*100/total,position,total }'
      break
    fi
  done
fi

free -h
df -h /
