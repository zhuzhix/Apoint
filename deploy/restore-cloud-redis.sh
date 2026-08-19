#!/usr/bin/env bash
set -euo pipefail

cd /opt/astock-redis

printf '%s  %s\n' \
  'b2188a75a74fea9df3c5d3b86a5ab068e29795d867dd809e05cf47eb522f2d12' \
  'migration/redis-data-20260815.tar.gz' | sha256sum --check --strict

docker load -i migration/redis-image.tar
docker compose --env-file .env down --remove-orphans
docker volume create astock-redis-cloud_redis-data >/dev/null

volume_name="$(docker volume inspect astock-redis-cloud_redis-data \
  --format '{{.Name}}')"
if [[ "${volume_name}" != 'astock-redis-cloud_redis-data' ]]; then
  printf 'Unexpected Redis volume: %s\n' "${volume_name}" >&2
  exit 1
fi

docker run --rm \
  -v astock-redis-cloud_redis-data:/target \
  -v /opt/astock-redis/migration:/backup:ro \
  redis:8 sh -lc \
  'find /target -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + &&
   tar -C /target -xzf /backup/redis-data-20260815.tar.gz'

docker compose --env-file .env up -d --pull never

for _ in $(seq 1 90); do
  status="$(docker inspect --format '{{.State.Health.Status}}' \
    astock-cloud-redis 2>/dev/null || true)"
  if [[ "${status}" == 'healthy' ]]; then
    break
  fi
  sleep 2
done

status="$(docker inspect --format '{{.State.Health.Status}}' \
  astock-cloud-redis 2>/dev/null || true)"
if [[ "${status}" != 'healthy' ]]; then
  docker logs --tail 100 astock-cloud-redis >&2 || true
  exit 1
fi

docker compose --env-file .env ps
docker exec astock-cloud-redis sh -lc '
  redis-cli --no-auth-warning -a "$REDIS_PASSWORD" DBSIZE
  redis-cli --no-auth-warning -a "$REDIS_PASSWORD" INFO memory |
    grep -E "^(used_memory_human|maxmemory_human|mem_fragmentation_ratio):"
  redis-cli --no-auth-warning -a "$REDIS_PASSWORD" INFO persistence |
    grep -E "^(aof_enabled|aof_current_size|aof_rewrite_in_progress):"
'

free -h
df -h /
