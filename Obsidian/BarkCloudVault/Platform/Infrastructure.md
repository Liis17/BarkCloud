# Инфраструктура BarkCloud

Parent: [[Architecture]] · See also: [[Tools/Builder]] · [[Web/SystemUpdates]] · [[Backend/ResumableUpload]]

## Назначение

`Backend/docker-compose.yml` описывает основной контейнерный стек. PostgreSQL, RabbitMQ, MinIO и Seq работают в общей Docker-сети с backend-сервисами и Web. Базовый compose не публикует порты на хост; внешний доступ задаётся отдельным reverse proxy.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/docker-compose.yml` | services, networks, volumes | Сервисы, зависимости и постоянные данные |
| `Backend/sample.env` | переменные окружения | Шаблон настройки compose |
| `Backend/nginx/cloud.barkfluff.conf` | upstream и server blocks | TLS, gRPC и HTTP-маршрутизация |

## Публичные контракты

| Контракт | Поведение |
|---|---|
| Docker-сеть `barkcloud-network` | Внешняя сеть; должна быть создана до запуска Compose |
| `CONFIGURATION_SERVICE_URL`, `CONFIGURATION_ACCESS_KEY` | Bootstrap-адрес и ключ для получения конфигурации сервисами |
| `IDENTITY_PORT`, `USERS_PORT`, `CONFIGURATION_PORT`, `FILES_PORT`, `FILES_HTTP1PORT`, `TORRENT_PORT`, `TORRENT_HTTP1PORT` | Внутренние gRPC и HTTP/1 порты |
| `EXTERNAL_*_HOST` | Публичные адреса сервисов для клиентских приложений |
| `WEB_COOKIE_SECURE`, `WEB_PUBLIC_HOST`, `WEB_ADMIN_PASSWORD` | Настройки браузерной cookie, публичного адреса и AdminGate |

## Зависимости и взаимодействия

- `cloud-identity`, `cloud-users`, `cloud-files`, `cloud-notification`, `cloud-torrent` и `cloud-web` получают общие параметры Configuration. Compose задаёт порядок запуска зависимостей через `depends_on`.
- `cloud-files` подключает данные MinIO для проверки диска read-only и отдельный каталог временных архивов. `cloud-torrent` хранит загружаемые торренты в `/mnt/torrents`.
- `cloud-web` получает `docker.sock`, compose-файл для записи, `.env` только для чтения и постоянный том обслуживания.
- Конфигурация nginx публикует gRPC backend-сервисов под TLS, проксирует HTTP/1 загрузки и скачивания Files/Torrent, а веб-клиент направляет на Web. Поток байтов resumable upload идёт через nginx напрямую в HTTP/1 Files; детали в [[Backend/ResumableUpload]].
- Для хранения используются тома или хост-пути: PostgreSQL и backup, MinIO, RabbitMQ, Seq, временные архивы, Torrent и состояние обслуживания Web.

## Ограничения и важные детали

- `cloud-nginx` не включён в базовый compose. Файл nginx предназначен для внешнего reverse proxy; генератор может добавить nginx в создаваемый compose, см. [[Tools/Builder]].
- Внутренние порты сервисов не опубликованы секцией `ports` базового compose. Прокси/внешняя инфраструктура отвечает за публикацию портов.
- Почта передаётся в Configuration переменными `EMAIL_HOST`, `EMAIL_PORT`, `EMAIL_SENDER_EMAIL` и `EMAIL_SENDER_PASSWORD`; отправку выполняет Notification.

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
