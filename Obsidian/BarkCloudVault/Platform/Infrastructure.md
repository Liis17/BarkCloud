# Инфраструктура BarkCloud

Parent: [[Architecture]] · See also: [[Tools/Builder]] · [[Web/SystemUpdates]] · [[Backend/ResumableUpload]]

## Назначение

`Backend/docker-compose.yml` описывает основной контейнерный стек. PostgreSQL, RabbitMQ, MinIO и Seq работают в общей Docker-сети с backend-сервисами и Web. Базовый compose не публикует порты на хост; внешний доступ задаётся отдельным reverse proxy.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/docker-compose.yml` | services, networks, volumes | Сервисы, зависимости и постоянные данные |
| `Backend/sample.env` | переменные окружения | Шаблон настройки compose |
| `Tools/BarkCloud.Builder/BackendComposeGenerator.cs` | `BuildLognaraDockerfiles` | Генерация вспомогательных Dockerfile для включённых инфраструктурных агентов |
| `Backend/nginx/cloud.barkfluff.conf` | upstream и server blocks | TLS, gRPC и HTTP-маршрутизация |

## Публичные контракты

| Контракт | Поведение |
|---|---|
| Docker-сеть `barkcloud-network` | Общая сеть сервисов и локального Lognara relay; Builder задаёт реальное имя через `DOCKER_NETWORK_NAME`, сеть нужно создать до запуска |
| `CONFIGURATION_SERVICE_URL`, `CONFIGURATION_ACCESS_KEY` | Bootstrap-адрес и ключ для получения конфигурации сервисами |
| `*_CONSOLE_OUTPUT`, `*_SEQ_ENABLE`, `*_LOGNARA_ENABLE` | Независимые локальные настройки sink для каждого из семи backend-приложений; defaults — Console on, Seq и Lognara off |
| `IDENTITY_PORT`, `USERS_PORT`, `CONFIGURATION_PORT`, `FILES_PORT`, `FILES_HTTP1PORT`, `TORRENT_PORT`, `TORRENT_HTTP1PORT` | Внутренние gRPC и HTTP/1 порты; значения по умолчанию: 8000, 8001, 8003, 8005/8006 и 8007/8008 |
| `EXTERNAL_*_HOST` | Публичные адреса сервисов для клиентских приложений |
| `WEB_COOKIE_SECURE`, `WEB_PUBLIC_HOST`, `WEB_TRUSTED_PROXY_NETWORKS`, `WEB_ADMIN_PASSWORD` | Настройки браузерной cookie, публичного адреса, доверенных reverse-proxy и AdminGate |

## Зависимости и взаимодействия

- `cloud-identity`, `cloud-users`, `cloud-files`, `cloud-notification`, `cloud-torrent` и `cloud-web` получают общие параметры Configuration. Compose задаёт порядок запуска зависимостей через `depends_on`; редактируемые Docker container names не меняют ключи сервисов и их DNS alias `cloud-*`.
- `cloud-files` подключает данные MinIO для проверки диска read-only и отдельный каталог временных архивов. `cloud-torrent` хранит загружаемые торренты в `/mnt/torrents`.
- `Backend/sample.env` задаёт относительные хост-пути `./data/minio`, `./data/pgdata`, `./data/pgbackup`, `./data/torrent` и `./data/archive-temp`; Seq-путь появляется при его включении. Пустой путь в Compose означает использование named volume. MinIO использует образ `docker.barkfluff.com/minio:RELEASE.2025-04-22T22-12-26Z-cpuv1`.
- `cloud-web` получает `docker.sock`, compose-файл для записи, `.env` только для чтения и постоянный том обслуживания.
- Приложения фиксируют локальные startup flags до загрузки удалённой конфигурации. Console, Seq и Lognara не зависят друг от друга; значения задаются через `.env`, а command-line параметры имеют приоритет над environment.
- При включении Lognara Compose добавляет локальный `lognara-relay` в `barkcloud-network`. Включённые агенты используют его по адресу `http://lognara-relay:7401/v1/batches`; relay пересылает данные на вручную указанный Core URL `/v1/batches`. Порты relay не публикуются на хост.
- Включённые инфраструктурные контейнеры nginx, MinIO, PostgreSQL, RabbitMQ и Seq получают отдельный сгенерированный agent image на основе исходного image. Wrapper сохраняет исходные `ENTRYPOINT`, `CMD`, `USER` и `STOPSIGNAL`; Compose `command` остаётся применимым. Внешний nginx не меняется. Для PostgreSQL Lognara включение отключает файловый logging collector и направляет логи в stderr; Seq не получает источники из своих файлов или БД.
- `Backend/nginx/cloud.barkfluff.conf` направляет gRPC backend-сервисов под TLS, HTTP/1 загрузки и скачивания Files/Torrent, и Web через отдельные upstream. Поток байтов resumable upload идёт через nginx напрямую в HTTP/1 Files; детали в [[Backend/ResumableUpload]].
- Все nginx upstream используют shared zone и разрешают Docker DNS через `127.0.0.11` с периодом 5 секунд. Это позволяет переживать пересоздание контейнеров без перезапуска nginx; требуется nginx OSS 1.27.3 или новее. Session cache использует имя `barkcloud_ssl`, чтобы не пересекаться с конфигами других приложений в общем nginx.
- Для хранения используются тома или хост-пути: PostgreSQL и backup, MinIO, RabbitMQ, Seq, временные архивы, Torrent и состояние обслуживания Web.

## Ограничения и важные детали

- `cloud-nginx` не включён в базовый compose. Builder может включить его в сгенерированный compose или оставить внешний nginx; при внешнем режиме nginx-контейнер подключается к реальному имени общей Docker-сети из `DOCKER_NETWORK_NAME`. См. [[Tools/Builder]].
- При переходе внешнего nginx со статических адресов нужно один раз вручную установить динамический upstream-конфиг и активировать его после подключения nginx к общей Docker-сети. Дальнейшее восстановление backend-адресов выполняется через Docker DNS без reload внешнего nginx. Интеграция Web для обслуживания описана в [[Web/SystemUpdates]].
- Внутренние порты сервисов не опубликованы секцией `ports` базового compose. Прокси/внешняя инфраструктура отвечает за публикацию портов. Приложения по умолчанию используют Identity 8000, Users 8001, Configuration 8003, Files 8005/8006 и Torrent 8007/8008; стандартные listener-порты PostgreSQL 5432, RabbitMQ 5672, MinIO 9000/9001, Seq 80/5341 и Web 8080 остаются внутри Docker-сети.
- Почта передаётся в Configuration переменными `EMAIL_HOST`, `EMAIL_PORT`, `EMAIL_SENDER_EMAIL` и `EMAIL_SENDER_PASSWORD`; отправку выполняет Notification.

## Lognara relay и агенты

Builder включает sink отдельно для каждого приложения. При включённом Lognara каждый выбранный агент получает свой вручную созданный token и spool volume или bind-путь. Relay использует `LOGNARA_CORE_URL` и `LOGNARA_CORE_TOKEN` для внешнего Core; он не регистрирует агентов автоматически. URL должен использовать HTTP или HTTPS и заканчиваться на `/v1/batches`.

Builder генерирует `lognara/*.Dockerfile` только для включённых инфраструктурных служб, если выбран локальный Lognara. Образы скачивают опубликованный release Linux amd64/arm64 только во время `docker build`, проверяют archive по `SHA256SUMS`, а версия выбирается `LOGNARA_AGENT_VERSION` (по умолчанию `latest`). Изменение infrastructure image metadata следует сверять через локальный `docker image inspect` перед обновлением исходных образов.

Named spool volumes разделены между агентами. Configuration использует существующее имя `lognara_configuration_spool` и uid/gid 1654; остальные приложения кроме Web также используют runtime uid 1654, Web и локальные infrastructure images запускаются от root, relay — uid 1000. В `.env` находятся tokens активных агентов. Поскольку Compose сохраняет полный `env_file` для Configuration и Web, token isolation между этими сервисами не обеспечивается.

Миграция выполняется в порядке: сначала новый Compose и `.env` вместе с созданными инфраструктурными Dockerfile helpers, затем новые app images. Существующие named spool volumes нужно сохранить. Новые app images, запущенные со старым Compose без явных startup flags, включат только Console и отключат Seq; старые образы сохраняют прежнее включение Seq. Старый Compose `entrypoint`-wrapper приложения следует убрать. При остановке agent run пересылает полученный сигнал остановки группе foreground-процессов (nginx получает `SIGQUIT`, PostgreSQL — `SIGINT`); после `SIGTERM`, `SIGINT` или `SIGQUIT` агент ждёт не более 10 секунд, затем отправляет группе `SIGKILL`. Compose задаёт 75 секунд приложению и 30 секунд инфраструктуре, но более длинный timeout Compose не увеличивает внутреннее окно agent run.

## Files upload scheduler: внедрение и откат

Scheduler работает внутри Files: MassTransit.Quartz 8.5.2 + Quartz 3.15.0,
PostgreSQL store в `FilesDb`, отдельная схема `files_quartz`. Durable очередь
`files-upload-scheduler` принимает команды расписания. Плагин брокера и дополнительный
контейнер scheduler не требуются. Основной compose продолжает использовать
`rabbitmq:latest` и постоянные данные PostgreSQL/RabbitMQ; версию брокера нужно
зафиксировать в результатах приёмочного прогона. Обновление/downgrade брокера не входит
в изменение механизма повторов. Publisher confirms MassTransit RabbitMQ остаются включены.

Внедрение: применить `20261009000000_AddUploadScheduler` → перезапустить Files →
smoke-проверить доставку `ProcessUploadedFile` и переход тестовой сессии в `Ready`.
Обычный старт Files сам применяет миграцию до старта scheduler.
Миграция создаёт Quartz-таблицы без DROP/очистки данных.

Контроль:

- `process-uploaded-file_error` — исчерпание необработанных исключений; сессия может
  остаться `Processing`, автоматического Fault-consumer нет.
- `files-upload-scheduler_error` — ошибки команд расписания; проверять сообщения
  вручную и устранять причину перед повторной доставкой.
- Ошибки `ScheduledMessageJob` и `ScheduledMessageRecoveryListener` в логах Files;
  предупреждение о сохранённом recovery-trigger содержит destination и trigger key.
- `files_quartz.qrtz_triggers` и `files_quartz.qrtz_fired_triggers` — ожидающие/активные
  доставки; `qrtz_scheduler_state` — состояние кластерных экземпляров scheduler.
  Проверять застрявшие trigger, ошибки PostgreSQL и backlog очередей.

При откате сохранять scheduler с новой интеграцией работающим до завершения ожидающих
доставок. Остановка всех таких экземпляров прекращает выдачу таймеров. Таблицы и
очереди не удалять: `Down` миграции сохраняет scheduler-данные. Приложение с прежними
локальными retry не исполняет сохранённые Quartz-таймеры самостоятельно.

Команды Docker-стенда и приёмки — [[Platform/Testing]] и
`Tests/Backend/BarkCloud.Files.IntegrationTests/README.md`.
