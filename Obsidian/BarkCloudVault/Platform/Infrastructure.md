# Инфраструктура BarkCloud

Parent: [[Architecture]] · See also: [[Tools/Builder]] · [[Web/SystemUpdates]] · [[Backend/ResumableUpload]]

## Назначение

`Backend/docker-compose.yml` описывает основной контейнерный стек. PostgreSQL, RabbitMQ, MinIO и Seq работают в общей Docker-сети с backend-сервисами и Web. Базовый compose не публикует порты на хост; внешний доступ задаётся отдельным reverse proxy.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/docker-compose.yml` | services, networks, volumes | Сервисы, зависимости и постоянные данные |
| `Backend/BarkCloud.Transcoder/Dockerfile` | `cloud-transcoder` | Изолированный AV1/WebM-транскодер с ffmpeg |
| `Backend/sample.env` | переменные окружения | Шаблон настройки compose |
| `Backend/provision-isolation.sh`, `provision-postgres.sql`, `provision-rabbitmq.sh` | service identities and permissions | Provisioning PostgreSQL и RabbitMQ |
| `Backend/isolation-guide.md` | operator procedure | Порядок установки, перехода и отката |
| `Tools/BarkCloud.Builder/BackendComposeGenerator.cs` | `BuildLognaraDockerfiles` | Генерация вспомогательных Dockerfile для включённых инфраструктурных агентов |
| `Backend/nginx/cloud.barkfluff.conf` | upstream и server blocks | TLS, gRPC и HTTP-маршрутизация |

## Публичные контракты

| Контракт | Поведение |
|---|---|
| Docker-сеть `barkcloud-network` | Общая сеть сервисов и локального Lognara relay; Builder задаёт реальное имя через `DOCKER_NETWORK_NAME`, сеть нужно создать до запуска |
| `CONFIGURATION_SERVICE_URL`, `CONFIGURATION_ACCESS_KEY` | Bootstrap-адрес и ключ для получения конфигурации сервисами |
| `*_CONSOLE_OUTPUT`, `*_SEQ_ENABLE`, `*_LOGNARA_ENABLE` | Независимые локальные настройки sink для каждого из семи backend-приложений; defaults — Console on, Seq и Lognara off |
| `IDENTITY_PORT`, `USERS_PORT`, `CONFIGURATION_PORT`, `FILES_PORT`, `FILES_HTTP1PORT`, `TORRENT_PORT`, `TORRENT_HTTP1PORT` | Внутренние gRPC и HTTP/1 порты; значения по умолчанию: 8000, 8001, 8003, 8005/8006 и 8007/8008 |
| `TRANSCODER_TOKEN`, `TRANSCODER_THREADS`, `TRANSCODER_CPUS`, `TRANSCODER_MEMORY`, `TRANSCODE_WORK_PATH` | Общий токен Files/Transcoder, параллелизм ffmpeg, лимиты контейнера и общая хост-папка |
| `EXTERNAL_*_HOST` | Публичные адреса сервисов для клиентских приложений |
| `WEB_COOKIE_SECURE`, `WEB_PUBLIC_HOST`, `WEB_TRUSTED_PROXY_NETWORKS`, `WEB_ADMIN_PASSWORD` | Настройки браузерной cookie, публичного адреса, доверенных reverse-proxy и AdminGate |

## Зависимости и взаимодействия

- `cloud-identity`, `cloud-users`, `cloud-files`, `cloud-notification`, `cloud-torrent` и `cloud-web` получают общие параметры Configuration. Изолированный `cloud-transcoder` не подключается к Configuration, БД или RabbitMQ; Compose задаёт порядок запуска Files после него через `depends_on`.
- `cloud-files` подключает данные MinIO для проверки диска read-only, каталог временных архивов и общую с `cloud-transcoder` папку `/mnt/transcode`. Sidecar слушает только внутри Docker-сети на 8080; host-порты не опубликованы. Доступ к API требует `X-Transcoder-Token`; compose передаёт один `TRANSCODER_TOKEN` обоим контейнерам. `cloud-torrent` хранит загружаемые торренты в `/mnt/torrents`.
- `Backend/sample.env` задаёт относительные хост-пути `./data/minio`, `./data/pgdata`, `./data/pgbackup`, `./data/torrent`, `./data/archive-temp` и `./data/transcode`; Seq-путь появляется при его включении. Пустой путь в Compose означает использование named volume. Для транскодера `TRANSCODER_THREADS` ограничивает ffmpeg, `TRANSCODER_CPUS` и `TRANSCODER_MEMORY` задают лимиты Docker; `TRANSCODE_WORK_PATH` монтируется одинаково в Files и sidecar. MinIO использует образ `docker.barkfluff.com/minio:RELEASE.2025-04-22T22-12-26Z-cpuv1`.
- `cloud-configuration` не использует `env_file`: получает только явные service-scoped PostgreSQL/RabbitMQ secrets и нужные bootstrap-настройки, без operator credentials. `cloud-web` остаётся доверенной admin-границей: работает от root и получает полный `.env`, Docker socket и compose-файл для записи.
- Приложения фиксируют локальные startup flags до загрузки удалённой конфигурации. Console, Seq и Lognara не зависят друг от друга; значения задаются через `.env`, а command-line параметры имеют приоритет над environment.
- При включении Lognara Compose добавляет локальный `lognara-relay` в `barkcloud-network`. Включённые агенты используют его по адресу `http://lognara-relay:7401/v1/batches`; relay пересылает данные на вручную указанный Core URL `/v1/batches`. Порты relay не публикуются на хост.
- Включённые инфраструктурные контейнеры nginx, MinIO, PostgreSQL, RabbitMQ и Seq получают отдельный сгенерированный agent image на основе исходного image. Wrapper сохраняет исходные `ENTRYPOINT`, `CMD`, `USER` и `STOPSIGNAL`; Compose `command` остаётся применимым. Внешний nginx не меняется. Для PostgreSQL Lognara включение отключает файловый logging collector и направляет логи в stderr; Seq не получает источники из своих файлов или БД.
- `Backend/nginx/cloud.barkfluff.conf` направляет gRPC backend-сервисов под TLS, HTTP/1 загрузки и скачивания Files/Torrent, и Web через отдельные upstream. Поток байтов resumable upload идёт через nginx напрямую в HTTP/1 Files; детали в [[Backend/ResumableUpload]].
- Все nginx upstream используют shared zone и разрешают Docker DNS через `127.0.0.11` с периодом 5 секунд. Это позволяет переживать пересоздание контейнеров без перезапуска nginx; требуется nginx OSS 1.27.3 или новее. Session cache использует имя `barkcloud_ssl`, чтобы не пересекаться с конфигами других приложений в общем nginx.
- Для хранения используются тома или хост-пути: PostgreSQL и backup, MinIO, RabbitMQ, Seq, временные архивы, рабочий каталог транскодирования, Torrent и состояние обслуживания Web.

## Ограничения и важные детали

- `cloud-nginx` не включён в базовый compose. Builder может включить его в сгенерированный compose или оставить внешний nginx; при внешнем режиме nginx-контейнер подключается к реальному имени общей Docker-сети из `DOCKER_NETWORK_NAME`. См. [[Tools/Builder]].
- При переходе внешнего nginx со статических адресов нужно один раз вручную установить динамический upstream-конфиг и активировать его после подключения nginx к общей Docker-сети. Дальнейшее восстановление backend-адресов выполняется через Docker DNS без reload внешнего nginx. Интеграция Web для обслуживания описана в [[Web/SystemUpdates]].
- Внутренние порты сервисов не опубликованы секцией `ports` базового compose. Прокси/внешняя инфраструктура отвечает за публикацию портов. Приложения по умолчанию используют Identity 8000, Users 8001, Configuration 8003, Files 8005/8006 и Torrent 8007/8008; стандартные listener-порты PostgreSQL 5432, RabbitMQ 5672, MinIO 9000/9001, Seq 80/5341 и Web 8080 остаются внутри Docker-сети.
- Почта передаётся в Configuration переменными `EMAIL_HOST`, `EMAIL_PORT`, `EMAIL_SENDER_EMAIL` и `EMAIL_SENDER_PASSWORD`; отправку выполняет Notification.

## Переход на разделённые секреты и RS256

`Backend/migrate-secrets.sh` заполняет отсутствующие или `replace-*` bootstrap-ключи Configuration, сервисные token credentials, `TRANSCODER_TOKEN` и индивидуальные PostgreSQL/RabbitMQ passwords, сохраняя заданные значения и восстанавливая пропущенные bootstrap/token values из старых реестров. Реестры `CONFIGURATION_ACCESS_KEYS` и `SERVICE_CREDENTIALS` являются производными от индивидуальных переменных и синхронизируются при каждом запуске. Builder помещает скрипт и provisioning/smoke artifacts в папку вывода. Скрипт создаёт резервную копию `.env`, но не меняет инфраструктуру.

Для новой установки сначала создайте PostgreSQL/RabbitMQ, выполните `provision-isolation.sh fresh`, затем запускайте приложения. Для существующей установки сделайте и проверьте backup PostgreSQL/RabbitMQ, остановите все приложения, затем установите новый `docker-compose.yml`, `.env` и provisioning files **до любого restart**. Запустите `migrate-secrets.sh`, затем `provision-isolation.sh existing` с `BARKCLOUD_BACKUP_VERIFIED=YES` и `BARKCLOUD_APPS_STOPPED=YES`; после provisioning запускайте сначала Configuration, затем остальные новые образы. Не смешивайте версии backend. Миграция `20261028120000_IsolateServiceCredentials` удаляет прежние root-based service connection strings и общие RabbitMQ credentials вместе с историей. Откат после provisioning требует восстановить предыдущие образы, compose, `.env`, PostgreSQL backup и RabbitMQ backup/definitions согласованно.

PostgreSQL roles `barkcloud_configuration`, `barkcloud_identity`, `barkcloud_users`, `barkcloud_files` и `barkcloud_torrent` не имеют elevated role attributes и подключаются только к собственной app DB. Provisioning переводит ownership только в схеме `public` каждой DB и `files_quartz` у Files, оставляя extension-owned объекты вроде `pg_trgm` владельцу extension; `PUBLIC CONNECT` к app DB отозван. RabbitMQ users Identity, Users, Files, Torrent и Notification получают отдельные ACL на настроенном существующем vhost. ACL сохраняет Users/Files outbox events, user-deleted consumers, Files Quartz scheduler commands и поток Identity → Notification для `EmailNotification`, но не даёт Users/Files/Torrent читать, публиковать или привязывать собственные очереди к email routes и `Fault<EmailNotification>`.

`POSTGRES_USER`/`POSTGRES_PASSWORD` и `RABBITMQ_DEFAULT_USER`/`RABBITMQ_DEFAULT_PASS` остаются операторскими credentials инфраструктуры. Configuration получает индивидуальные passwords для создания service-scoped settings, но Compose не передаёт ему эти operator credentials. Web намеренно остаётся доверенным: он запускается от root, получает полный `.env` и Docker socket, поэтому компрометация Web даёт административный доступ к deployment.

Builder создаёт приватный JWT PEM на Windows. После копирования вывода на Linux запустите `migrate-secrets.sh` от владельца ключа. Скрипт оставляет чтение владельцу и выдаёт его только UID 1654 через ACL; группа и остальные пользователи доступа не получают. Если `setfacl` недоступен, запуск от root передаёт файл UID 1654 с mode `600`. Не задавайте общий режим чтения `644` для private PEM.

Переход на RS256 может сделать старые access JWT неприемлемыми для новых валидаторов. `RemoveLegacySharedSecrets` не удаляет refresh-записи из Identity DB; действующие refresh-сессии могут получить новые access JWT, если Identity DB и согласованная пара RSA ключей сохранены. При откате на прежнюю версию нужно вернуть соответствующую ей Configuration DB, где старые настройки ещё присутствуют.

## Lognara relay и агенты

Builder включает sink отдельно для каждого приложения. При включённом Lognara каждый выбранный агент получает свой вручную созданный token и spool volume или bind-путь. Relay использует `LOGNARA_CORE_URL` и `LOGNARA_CORE_TOKEN` для внешнего Core; он не регистрирует агентов автоматически. URL должен использовать HTTP или HTTPS и заканчиваться на `/v1/batches`.

Builder генерирует `lognara/*.Dockerfile` только для включённых инфраструктурных служб, если выбран локальный Lognara. Образы скачивают опубликованный release Linux amd64/arm64 только во время `docker build`, проверяют archive по `SHA256SUMS`, а версия выбирается `LOGNARA_AGENT_VERSION` (по умолчанию `latest`). Изменение infrastructure image metadata следует сверять через локальный `docker image inspect` перед обновлением исходных образов.

Named spool volumes разделены между агентами. Configuration использует существующее имя `lognara_configuration_spool` и uid/gid 1654; остальные приложения кроме Web также используют runtime uid 1654, Web и локальные infrastructure images запускаются от root, relay — uid 1000. В `.env` находятся tokens активных агентов. Web получает полный `.env`, поэтому остаётся доверенной границей deployment secrets; Configuration получает только явно переданные необходимые значения.

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
