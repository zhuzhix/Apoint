#!/usr/bin/env bash
set -euo pipefail

container_name="${MYSQL_CONTAINER_NAME:-astock-cloud-mysql}"

docker exec "${container_name}" sh -lc \
  'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" --batch --skip-column-names astock_monitor -e "
    DROP TABLE IF EXISTS pair_trend_event_compact;
    CREATE TABLE pair_trend_event_compact LIKE pair_trend_event;
    SHOW CREATE TABLE pair_trend_event_compact;
    DROP TABLE pair_trend_event_compact;
  "'
