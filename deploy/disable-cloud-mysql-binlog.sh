#!/usr/bin/env bash
set -euo pipefail

base_dir="${ASTOCK_DATA_DIR:-/opt/astock-data}"
compose_file="${base_dir}/docker-compose.yml"
container_name="${MYSQL_CONTAINER_NAME:-astock-cloud-mysql}"

cd "${base_dir}"
docker compose -f "${compose_file}" up -d mysql

for _ in $(seq 1 90); do
  health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "${container_name}" 2>/dev/null || true)"
  [[ "${health}" == "healthy" ]] && break
  sleep 2
done
[[ "${health:-}" == "healthy" ]] || { echo "MySQL did not become healthy." >&2; exit 1; }

log_bin="$(docker exec "${container_name}" sh -lc \
  'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --batch --skip-column-names -e "SELECT @@GLOBAL.log_bin;"')"
[[ "${log_bin}" == "0" ]] || { echo "log_bin is still enabled; refusing file cleanup." >&2; exit 1; }

volume_name="$(docker inspect "${container_name}" --format '{{range .Mounts}}{{if eq .Destination "/var/lib/mysql"}}{{.Name}}{{end}}{{end}}')"
[[ -n "${volume_name}" ]] || { echo "MySQL volume not found." >&2; exit 1; }
mountpoint="$(docker volume inspect "${volume_name}" --format '{{.Mountpoint}}')"
case "${mountpoint}" in
  /var/lib/docker/volumes/*/_data) ;;
  *) echo "Unexpected MySQL volume mountpoint: ${mountpoint}" >&2; exit 1 ;;
esac

docker compose -f "${compose_file}" stop mysql
find "${mountpoint}" -maxdepth 1 -type f \( -name 'binlog.*' -o -name 'binlog.index' \) -print
find "${mountpoint}" -maxdepth 1 -type f \( -name 'binlog.*' -o -name 'binlog.index' \) -delete
docker compose -f "${compose_file}" start mysql

for _ in $(seq 1 90); do
  health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "${container_name}" 2>/dev/null || true)"
  [[ "${health}" == "healthy" ]] && break
  sleep 2
done
[[ "${health:-}" == "healthy" ]] || { echo "MySQL did not recover after binlog cleanup." >&2; exit 1; }

docker exec "${container_name}" sh -lc \
  'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --batch --skip-column-names -e "SELECT @@GLOBAL.log_bin, @@GLOBAL.innodb_flush_log_at_trx_commit, @@GLOBAL.sync_binlog;"'
du -sh "${mountpoint}"
df -h "${mountpoint}"
