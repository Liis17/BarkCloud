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
