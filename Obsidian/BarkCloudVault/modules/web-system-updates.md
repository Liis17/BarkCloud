# Backend — Web: Обслуживание (обновление бэкенда)

Parent: [[modules/backend-web]] · See also: [[index]] · [[structure/infrastructure]]

## Назначение

Управление обновлением и перезапуском микросервисов бэкенда прямо со страницы настроек веба (`/settings` → раздел «Обслуживание»). Логика перенесена из админ-панели BarkFluff (`Barkfluff.AdminPanel`) под один локальный хост — без SSH и удалённых серверов. Веб-контейнер через смонтированный `docker.sock` запускает `docker` / `docker compose`.

## Доступ

В BarkCloud нет ролей. Раздел закрыт отдельным паролем `App:AdminPassword` (env `WEB_ADMIN_PASSWORD`) — облако self-hosted «для своих».
- `Auth/AdminGate.cs`: `Unlock` сверяет пароль и выдаёт HttpOnly-cookie `bark_admin` — подписанный токен (HMAC-SHA256 на общем `JwtSettings:SecretKey`, срок 30 мин). `IsUnlocked` валидирует подпись и срок, `Lock` сбрасывает.
- Все `/api/system/*` требуют **и** авторизованного пользователя (`bark_at`), **и** разблокировки (`bark_admin`), кроме `unlock` (нужен только пользователь).

## Управляемый набор

Только сервисы приложения + сам веб (инфраструктуру postgres/minio/rabbitmq/seq/nginx **не трогаем**). Маппинг сервис → контейнер:
`configuration→cloud-configuration`, `identity→cloud-identity`, `users→cloud-users`, `files→cloud-files`, `notification→cloud-notification`, `web→cloud-web`.

## Механизм

`Infrastructure/DockerService.cs` (Process + `ArgumentList`, без shell):
- **Обновить сервис:** `docker compose -p <project> --env-file /.env -f /docker-compose.yml pull <svc>` → `up --force-recreate -d <svc>` → `docker image prune -f`.
- **Обновить всё:** последовательно `configuration → identity → users → files` (web исключён).
- **restart / start / stop:** `docker <action> <container>` по белому списку (web запрещён — только self-методы).
- **Self-restart веба:** detached **helper-контейнер** из образа самого web (`docker run -d --rm` c `sh -c "sleep 2 && docker restart cloud-web"`). Веб не может перезапустить себя изнутри. Имя compose-проекта — из метки `com.docker.compose.project`.
- **Self-update веба** (`UpdateWebSelfAsync`): **compose не используется** — он требует резолва относительных путей web (`./docker-compose.yml`, `./.env`) в реальные хостовые, что невозможно из Linux-helper'а на Windows-путях (`C:\…`). Вместо этого web пересоздаётся **клонированием**: `BuildWebRecreateSpecAsync` читает `docker inspect cloud-web` и собирает аргументы `docker run` из текущей конфигурации (image-tag, env целиком, labels включая `com.docker.compose.*`, порты, mounts с теми же источниками что уже знает демон, сеть+aliases). Detached helper выполняет `BuildSelfUpdateScript`: `pull` → `rename cloud-web→cloud-web-bak` → `stop bak` → `docker run` новый → при успехе `rm bak` + `image prune -f`, **при сбое — откат** (`rm` нового, `rename bak→cloud-web`, `start`). Источники mount'ов берутся из inspect как есть, поэтому работает **и под Linux/WSL, и на Windows Docker Desktop** — проверка `IsWindowsPath` удалена. Аргументы экранируются `ShQuote` (single-quote) для `sh -c`.
- Registry `docker.barkfluff.com:5000` **публичный на pull** (auth нужен только для push в CI), поэтому креды не требуются. `DOCKER_CONFIG=/tmp/barkcloud-docker` (пустой) — чтобы CLI не читал `config.json` хоста с `"credsStore": "desktop"` и не звал отсутствующий `docker-credential-desktop`. `config.json` хоста больше не монтируется.

## Эндпоинты (`SystemEndpoints.cs`)

`GET /healthz` (анонимный, для опроса страницей при self-update). Группа `/api/system`:
`POST /unlock`, `POST /lock`, `GET /services`, `POST /services/{svc}/{update|restart|start|stop}`, `POST /update-all`, `POST /web/update-self`, `POST /web/restart-self`.

Данные для рендера: `PageDataBuilder.BuildSettingsJsonAsync` добавляет в payload `admin{enabled,unlocked}` и `system{version,edition}`.

## UI

`Pages/Settings.html` — раздел «Обслуживание» (компонент `SystemSection`, M3-стиль `shared.css`):
- не настроен пароль → заглушка; не разблокировано → поле пароля; разблокировано → список сервисов (статус-pill, тег образа, кнопки обновить/перезапустить/стоп-старт), «Обновить всё», «Заблокировать».
- progress-модалка для «Обновить всё» (последовательные вызовы с прогресс-баром).
- full-screen overlay при self-update/-restart веба: опрашивает `/healthz` каждые 3с, после 2 успехов перезагружает страницу.
- Иконки `refresh`/`power` добавлены в `shared.jsx`.

## Инфраструктура

- `BarkCloud.Web/Dockerfile.slim` (**его использует prod-CI** `build-backend-web.yml`) и `Dockerfile`: финальный образ `aspnet:10.0-alpine` + `apk add docker-cli docker-cli-compose icu-libs tzdata` (chiseled не подходит — нет shell/пакетов), `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`, без `USER` (root задаётся в compose).
- `docker-compose.yml` сервис `web`: `user: root`, volumes `docker.sock`/`./docker-compose.yml`/`./.env` (монтирование `~/.docker/config.json` убрано — registry публичный), env `App__AdminPassword`.
- `sample.env`: `WEB_ADMIN_PASSWORD`.

## Компромисс безопасности

В BarkFluff это был изолированный admin-сервис; здесь те же права (`docker.sock`+root → полный контроль над хостом) получает публичный веб. Митигация: отдельный пароль + подписанная cookie, белый список из 5 сервисов, веб себя не stop/start (только self-update/-restart).
