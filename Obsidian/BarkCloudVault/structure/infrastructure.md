# Инфраструктура

Parent: [[index]] · See also: [[structure/overview]] · [[structure/entrypoints]]

Файлы: `Backend/docker-compose.yml` (prod), `Backend/docker-compose-dev.yml` (dev)

## Микросервисы в dev-окружении

Все на образах `barkcloud-*-dev:latest`, требующих локальной сборки из соответствующих `Dockerfile`. Все цепляются в общую сеть `barkcloud-network`.

- `configuration` — [[modules/backend-configuration]]
- `identity` — [[modules/backend-identity]]
- `users` — [[modules/backend-users]]
- `files` — [[modules/backend-files]]
- `torrent` — [[modules/backend-torrent]] (MonoTorrent; скачивание на том `/mnt/torrents`)
- `web` — [[modules/backend-web]] (HTTP-веб-клиент; не gRPC-сервис). Публикует `${WEB_PORT}:8080`; `depends_on`: configuration, identity, users, files, torrent.

Все микросервисы (кроме `configuration`) объявлены с `depends_on: configuration`.

## nginx reverse-proxy

`Backend/nginx/cloud.barkfluff.conf` — конфиг внешнего nginx (на хосте/перед compose, не отдельный сервис в dev-compose). Терминирует TLS на едином субдомене `cloud.barkfluff.com` и проксирует к сервисам по **внешнему порту**, внутрь — h2c (plaintext gRPC). Сертификат самоподписанный, поэтому клиенты доверяют всем (Android/iOS).

> **Keepalive (производительность):** апстримы оформлены `upstream`-блоками с `keepalive` — nginx держит пул постоянных h2c-соединений к бэкендам и не устанавливает TCP+HTTP/2 на каждый RPC (ранее `grpc_pass $variable` это делал, что душило rps). Размен: имена бэкендов резолвятся при загрузке конфига; после **пересоздания** backend-контейнера (обновление образа → новый IP) nginx ходит на старый IP до перезагрузки — выполнить `docker exec cloud-nginx nginx -s reload` (важно для self-update через web).

| Внешний порт (TLS) | Внутренний backend |
|---|---|
| `7020` | `grpc://cloud-identity:7000` |
| `7021` | `grpc://cloud-users:7021` |
| `7025` | `grpc://cloud-files:7025` (gRPC) + `http://cloud-files:7026` под `/web/` (скачивание/загрузка файлов) |
| `7027` | `grpc://cloud-torrent:7027` (gRPC) + `http://cloud-torrent:7028` под `/web/` (Range-скачивание) |

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
- `TORRENT_PORT/TORRENT_HTTP1PORT/TORRENT_PEER_PORT` — gRPC, HTTP1 и BitTorrent peer-порты торрент-сервиса
- `TORRENT_DOWNLOAD_PATH` — путь на хосте для скачанных торрентов; по умолчанию named volume `torrent_data`
- `EXTERNAL_TORRENT_HOST` — внешний gRPC-адрес торрент-сервиса для клиентов

Файл `.env` рядом с compose-файлом обязателен; шаблон — `Backend/sample.env`.

## Volumes

- `pgdata` — данные PostgreSQL по умолчанию (named volume); переопределяется через `POSTGRES_DATA_PATH` — см. «Переносимый диск с данными БД» ниже
- `rabbitmq_data` — данные RabbitMQ
- `minio_data` — данные MinIO по умолчанию (named volume). Источник `/data` переопределяется через `MINIO_DATA_PATH` в `.env`; тот же источник монтируется в `cloud-files` read-only как `/mnt/minio-data` для расчёта физического объёма диска. Вынос на отдельный диск — см. раздел «MinIO на отдельном диске» ниже.
- `backup_volume` — бэкапы Postgres (монтируется в Postgres-контейнер на `/backup`); переопределяется через `BACKUP_PATH`
- `seq_data` — данные Seq; переопределяется через `SEQ_DATA_PATH`
- `archive_temp` — временный файл ZIP при «Скачать архивом» (монтируется в `cloud-files` как `/mnt/archive-temp`, путь читается из env `Archive__TempPath`); переопределяется через `ARCHIVE_TEMP_PATH`. Только в прод-`docker-compose.yml`. Сценарий: zip собирается на диск → заливается в S3 → temp удаляется; готовый архив кладётся в корзину со сроком 3 дня (переиспользует фоновую очистку `TrashCleanupService`). Вынести на второй диск (где больше места, чем в образе) — `ARCHIVE_TEMP_PATH=/d/barkcloud/archive-temp`. Папка на NTFS/drvfs здесь годится (последовательная запись файла, без БД-семантики).
- `torrent_data` — скачанные торрент-файлы, монтируется в `cloud-torrent` как `/mnt/torrents`; переопределяется через `TORRENT_DOWNLOAD_PATH`.

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

## Переносимый диск с данными БД (ext4-vhdx, Windows/WSL2)

> Зачем: named volumes `pgdata`/`backup_volume` лежат внутри образа диска Docker (WSL2, обычно на C:) — их нельзя просто перенести на другой ПК. Чтобы БД «переезжала» вместе с диском, выноси её на **ext4**-том. Виндовую NTFS-папку (`/d/...` через drvfs) для Postgres использовать **нельзя**: на ней Postgres не стартует (нет Unix-прав, `fsync`, файловых блокировок). Нужен ext4 — в виде vhdx, смонтированного в WSL2.

**Раскладка по сервисам:**

| Сервис | Куда | Почему |
|---|---|---|
| Postgres (`POSTGRES_DATA_PATH`) | **отдельный** ext4-vhdx | нужна POSIX-ФС; свой vhdx |
| Бэкапы (`BACKUP_PATH`) | **отдельный** ext4-vhdx | раздельно с данными — если vhdx с БД повредится, бэкапы на втором уцелеют |
| MinIO (`MINIO_DATA_PATH`) | отдельными блобами на диске (раздел «MinIO на отдельном диске» выше) | vhdx — один файл; его повреждение = потеря **всех** файлов сразу. Блобы по-файлово изолируют потери |
| RabbitMQ | остаётся в образе WSL (named volume `rabbitmq_data`) | данные — транзиентные очереди, при переносе не нужны |
| Seq (`SEQ_DATA_PATH`) | опционально, папкой на диске | логи; интегритет не критичен, vhdx не нужен. По умолчанию — в образе |

Два vhdx именно **раздельные**: цель бэкапов — пережить порчу основного тома, поэтому они не должны делить с ним один файл.

**1. Создать два динамических vhdx** (cmd, diskpart — без Hyper-V):
```
diskpart
  create vdisk file="D:\barkcloud\postgres.vhdx" maximum=256000 type=expandable
  create vdisk file="D:\barkcloud\backup.vhdx"   maximum=256000 type=expandable
  exit
```
`maximum` в МБ, `expandable` = растёт по мере заполнения.

**2. Отформатировать ext4 и смонтировать каждый с именем** (⚠️ `lsblk` → не перепутай устройство, `mkfs` затирает данные):
```
# postgres.vhdx → /mnt/wsl/pg
wsl --mount --vhd "D:\barkcloud\postgres.vhdx" --bare
lsblk                                   # найти новое устройство, напр. /dev/sdX
sudo mkfs.ext4 /dev/sdX
wsl --unmount "D:\barkcloud\postgres.vhdx"
wsl --mount --vhd "D:\barkcloud\postgres.vhdx" --name pg

# backup.vhdx → /mnt/wsl/backup
wsl --mount --vhd "D:\barkcloud\backup.vhdx" --bare
lsblk                                   # теперь новое устройство, напр. /dev/sdY
sudo mkfs.ext4 /dev/sdY
wsl --unmount "D:\barkcloud\backup.vhdx"
wsl --mount --vhd "D:\barkcloud\backup.vhdx" --name backup
```

**3. Прописать пути в `Backend/.env`:**
```
POSTGRES_DATA_PATH=/mnt/wsl/pg
BACKUP_PATH=/mnt/wsl/backup
# MinIO — отдельными блобами на диске (не в vhdx), см. раздел MinIO:
MINIO_DATA_PATH=/d/barkcloud/minio
```

**4. (Если уже есть данные в named volumes)** перенеси каждый том на свой vhdx:
```
docker compose -f docker-compose-dev.yml down
docker run --rm -v backend_pgdata:/from        -v /mnt/wsl/pg:/to     alpine sh -c "cp -a /from/. /to/"
docker run --rm -v backend_backup_volume:/from -v /mnt/wsl/backup:/to alpine sh -c "cp -a /from/. /to/"
```

**5. Поднять стек:**
```
docker compose -f docker-compose-dev.yml up -d
```

**Перенос на другой ПК:** скопируй `postgres.vhdx` и `backup.vhdx` (+ папку MinIO-блобов с диска) на второй ПК → `wsl --mount --vhd "...\postgres.vhdx" --name pg` и `--name backup` → те же пути в `.env` → `up -d`. Данные подхватятся.

> ⚠️ Персистентность: `wsl --mount` не переживает reboot/`wsl --shutdown` и должен выполняться **до** старта контейнеров (иначе Postgres создаст пустые данные на C:). Автоматизируй монтирование обоих vhdx перед запуском Docker (Task Scheduler «при входе» → `.bat` с `wsl --mount` + запуск Docker Desktop).

## Запуск dev-окружения

```bash
cd Backend
# собрать образы микросервисов из Dockerfile (или Dockerfile.slim)
docker compose -f docker-compose-dev.yml up -d
```
