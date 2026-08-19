#!/usr/bin/env bash
set -euo pipefail

install -d -m 700 /opt/astock-redis/migration
install -m 600 /tmp/docker-compose.cloud-redis.yml \
  /opt/astock-redis/docker-compose.yml

cd /opt/astock-redis

if [[ ! -f .env ]]; then
  umask 077
  redis_password="$(openssl rand -hex 24)"
  printf '%s\n' \
    "DATA_BIND_IP=172.29.221.217" \
    "REDIS_PASSWORD=${redis_password}" > .env
fi

chmod 600 .env docker-compose.yml
