#!/usr/bin/env bash
set -euo pipefail

# Поднимает PostgreSQL/RabbitMQ для тестов. services: в workflow не умеет ждать
# дольше нескольких секунд, а Docker Hub периодически отдаёт таймауты и toomanyrequests.

pull_attempts=5

pull_with_retry() {
  local image="$1" attempt=1
  until docker pull "$image"; do
    if [ "$attempt" -ge "$pull_attempts" ]; then
      echo "::error::Не удалось загрузить $image за $pull_attempts попыток"
      return 1
    fi
    echo "Повтор загрузки $image через $((attempt * 20)) с ($attempt/$pull_attempts)"
    sleep $((attempt * 20))
    attempt=$((attempt + 1))
  done
}

wait_healthy() {
  local name="$1"
  for _ in $(seq 1 60); do
    if [ "$(docker inspect -f '{{.State.Health.Status}}' "$name")" = healthy ]; then
      return 0
    fi
    sleep 2
  done
  echo "::error::Контейнер $name не стал healthy"
  docker logs "$name" || true
  return 1
}

if [ "${START_POSTGRES:-false}" = true ]; then
  pull_with_retry postgres:18
fi
if [ "${START_RABBITMQ:-false}" = true ]; then
  pull_with_retry rabbitmq:4.1
fi

if [ "${START_POSTGRES:-false}" = true ]; then
  docker run -d --name test-postgres \
    -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=postgres \
    -p 5432:5432 \
    --health-cmd "pg_isready -U postgres -d postgres" \
    --health-interval 5s --health-timeout 5s --health-retries 10 \
    postgres:18
fi
if [ "${START_RABBITMQ:-false}" = true ]; then
  docker run -d --name test-rabbitmq \
    -e RABBITMQ_DEFAULT_USER=integration -e RABBITMQ_DEFAULT_PASS=integration \
    -p 5672:5672 \
    --health-cmd "rabbitmq-diagnostics -q ping" \
    --health-interval 5s --health-timeout 5s --health-retries 10 \
    rabbitmq:4.1
fi

if [ "${START_POSTGRES:-false}" = true ]; then
  wait_healthy test-postgres
fi
if [ "${START_RABBITMQ:-false}" = true ]; then
  wait_healthy test-rabbitmq
fi
