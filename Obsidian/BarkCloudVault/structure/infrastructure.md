# Инфраструктура

Parent: [[index]] · See also: [[structure/overview]] · [[structure/entrypoints]]

Файл: `Backend/docker-compose-dev.yml`

## Микросервисы в dev-окружении

Все на образах `barkcloud-*-dev:latest`, требующих локальной сборки из соответствующих `Dockerfile`. Все цепляются в общую сеть `barkcloud-network`.

- `configuration` — [[modules/backend-configuration]]
- `identity` — [[modules/backend-identity]]
- `users` — [[modules/backend-users]]
- `files` — [[modules/backend-files]]

Все микросервисы (кроме `configuration`) объявлены с `depends_on: configuration`.

## Инфраструктурные контейнеры

| Сервис | Образ | Назначение |
|--------|-------|-----------|
| `postgres_barkcloud` | `postgres:18` | Единая PostgreSQL для всех сервисов (схемы изолируют) |
| `rabbitmq` | `rabbitmq:latest` | Очередь сообщений между сервисами; контракты в [[modules/shared-queue]] |
| `minio` | `quay.io/minio/minio` | S3-совместимое хранилище для файлов, аватаров, стикеров |
| `seq` | `datalust/seq:latest` | Централизованный лог-агрегатор; логи через Serilog |

## Переменные окружения (из .env)

Используются в compose:

- `CONFIGURATION_SERVICE_URL` — URL gRPC сервиса конфигурации (раздаётся всем)
- `ASPNETCORE_ENVIRONMENT`
- `CONFIGURATION_HOST/DATABASE/USERNAME/PASSWORD/PORT` — БД самого Configuration
- `POSTGRES_USER/PASSWORD/DB/PORT` — общая Postgres
- `MINIO_ROOT_USER/PASSWORD/PORT/WEBPORT`
- `RABBITMQ_DEFAULT_USER/PASS`
- `SEQ_ADMIN_PASSWORD/WEBPORT`

Файл `.env` рядом с `docker-compose-dev.yml` обязателен.

## Volumes

- `pgdata` — данные PostgreSQL
- `rabbitmq_data` — данные RabbitMQ
- `minio_data` — данные MinIO
- `backup_volume` — бэкапы (монтируется в Postgres-контейнер на `/backup`)
- `seq_data` — данные Seq

## Запуск dev-окружения

```bash
cd Backend
# собрать образы микросервисов из Dockerfile (или Dockerfile.slim)
docker compose -f docker-compose-dev.yml up -d
```
