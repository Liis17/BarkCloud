# Инфраструктура

Parent: [[index]] · See also: [[structure/overview]] · [[structure/entrypoints]]

Файл: `Backend/docker-compose-dev.yml`

## Микросервисы в dev-окружении

Все на образах `barkcloud-*-dev:latest`, требующих локальной сборки из соответствующих `Dockerfile`. Все цепляются в общую сеть `barkcloud-network`.

- `configuration` — [[modules/backend-configuration]]
- `identity` — [[modules/backend-identity]]
- `users` — [[modules/backend-users]]
- `files` — [[modules/backend-files]]
- `web` — [[modules/backend-web]] (HTTP-веб-клиент; не gRPC-сервис). Единственный с проброшенным портом наружу: `${WEB_PORT}:8080`. `depends_on`: configuration, identity, users, files.

Все микросервисы (кроме `configuration`) объявлены с `depends_on: configuration`.

## nginx reverse-proxy

`Backend/nginx/cloud.barkfluff.conf` — конфиг внешнего nginx (на хосте/перед compose, не отдельный сервис в dev-compose). Терминирует TLS на едином субдомене `cloud.barkfluff.com` и проксирует к сервисам по **внешнему порту**, внутрь — h2c (plaintext gRPC). Сертификат самоподписанный, поэтому клиенты доверяют всем (Android/iOS).

> **Keepalive (производительность):** апстримы оформлены `upstream`-блоками с `keepalive` — nginx держит пул постоянных h2c-соединений к бэкендам и не устанавливает TCP+HTTP/2 на каждый RPC (ранее `grpc_pass $variable` это делал, что душило rps). Размен: имена бэкендов резолвятся при загрузке конфига; после **пересоздания** backend-контейнера (обновление образа → новый IP) nginx ходит на старый IP до перезагрузки — выполнить `docker exec cloud-nginx nginx -s reload` (важно для self-update через web).

| Внешний порт (TLS) | Внутренний backend |
|---|---|
| `7020` | `grpc://cloud-identity:7000` |
| `7021` | `grpc://cloud-users:7021` |
| `7025` | `grpc://cloud-files:7025` (gRPC) + `http://cloud-files:7026` под `/web/` (скачивание/загрузка файлов) |

> Backend-порты соответствуют `RunSettings:Port` сервисов в конфиг-БД; при их смене — править конфиг.

## Инфраструктурные контейнеры

| Сервис | Образ | Назначение |
|--------|-------|-----------|
| `postgres_barkcloud` | `postgres:18` | Единая PostgreSQL для всех сервисов (схемы изолируют). В прод-`docker-compose.yml` запускается с тюнингом через `command` (`-c shared_buffers=1GB`, `effective_cache_size=3GB`, `work_mem=16MB`, `jit=off`, параллелизм под 2 ядра) — дефолты PG18 (128MB) под 8 ГБ малы. Движок **не меняем**: тормозили seq-scan'ы из-за отсутствия индексов + дефолтный конфиг, а не сам Postgres. |
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
- `WEB_PORT` — host-порт веб-клиента; `WEB_COOKIE_SECURE`, `WEB_PUBLIC_HOST` — UI-настройки web (JwtSettings и адреса сервисов web берёт из Configuration).

Файл `.env` рядом с `docker-compose-dev.yml` обязателен; шаблон — `Backend/sample.env`.

## Volumes

- `pgdata` — данные PostgreSQL
- `rabbitmq_data` — данные RabbitMQ
- `minio_data` — данные MinIO по умолчанию (named volume). Источник `/data` переопределяется через `MINIO_DATA_PATH` в `.env`; тот же источник монтируется в `cloud-files` read-only как `/mnt/minio-data` для расчёта физического объёма диска. Вынос на отдельный диск — см. раздел «MinIO на отдельном диске» ниже.
- `backup_volume` — бэкапы (монтируется в Postgres-контейнер на `/backup`)
- `seq_data` — данные Seq

## MinIO на отдельном диске (Windows/WSL2)

> Зачем: named volume `minio_data` лежит внутри образа диска Docker (обычно на C:) и растёт вместе с загрузками в S3. Чтобы хранить S3-данные на втором диске (D:), укажи путь к папке на нём через `MINIO_DATA_PATH` (по умолчанию — named volume, поведение не меняется).

**1. Создай папку на D:**, напр. `D:\barkcloud\minio`.

**2. Укажи путь в `Backend/.env`.** Форма для Docker Desktop, когда compose запускается из Windows-шелла (cmd/PowerShell):
```
MINIO_DATA_PATH=/d/barkcloud/minio
```
Если compose запускаешь изнутри WSL2 — путь к тому же диску будет `/mnt/d/barkcloud/minio`.

**3. Перенеси существующие данные** (чтобы не потерять текущий S3-контент):
```
docker volume ls                      # найти имя, напр. backend_minio_data
docker compose -f docker-compose-dev.yml stop minio
docker run --rm -v backend_minio_data:/from -v /d/barkcloud/minio:/to alpine \
  sh -c "cp -a /from/. /to/"          # копирует и скрытый .minio.sys
```

**4. Подними MinIO:**
```
docker compose -f docker-compose-dev.yml up -d minio
```

> ⚠️ Папка на NTFS-диске Windows пробрасывается в контейнер через drvfs/9p — без Unix-прав, xattr и атомарных rename. MinIO официально такие ФС **не поддерживает**: под нагрузкой возможны ошибки и повреждение данных. Это не зависит от того, как написан путь (`/d/...` через Docker Desktop или `/mnt/d/...` изнутри WSL) — под капотом тот же NTFS через drvfs. Надёжный (но более громоздкий) вариант — отдельный **ext4-vhdx**, смонтированный в WSL2 через `wsl --mount`, и `MINIO_DATA_PATH=/mnt/wsl/minio`.

## Запуск dev-окружения

```bash
cd Backend
# собрать образы микросервисов из Dockerfile (или Dockerfile.slim)
docker compose -f docker-compose-dev.yml up -d
```
