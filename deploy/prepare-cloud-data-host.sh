#!/usr/bin/env bash
set -euo pipefail

install -d -m 700 /opt/astock-data
install -m 600 /tmp/docker-compose.cloud-data.yml \
  /opt/astock-data/docker-compose.yml

cd /opt/astock-data

if [[ ! -f .env ]]; then
  umask 077
  mysql_root_password="$(openssl rand -hex 24)"
  mysql_app_password="$(openssl rand -hex 24)"
  redis_password="$(openssl rand -hex 24)"

  printf '%s\n' \
    "DATA_BIND_IP=172.21.62.193" \
    "MYSQL_ROOT_PASSWORD=${mysql_root_password}" \
    "MYSQL_APP_PASSWORD=${mysql_app_password}" \
    "REDIS_PASSWORD=${redis_password}" > .env
fi

chmod 600 .env docker-compose.yml
docker compose --env-file .env pull
docker compose --env-file .env up -d
docker compose --env-file .env ps
