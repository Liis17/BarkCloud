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
`configuration→cloud-configuration`, `identity→cloud-identity`, `users→cloud-users`, `files→cloud-files`, `web→cloud-web`.

## Механизм

`Infrastructure/DockerService.cs` (Process + `ArgumentList`, без shell):
- **Обновить сервис:** `docker compose -p <project> --env-file /.env -f /docker-compose.yml pull <svc>` → `up --force-recreate -d <svc>` → `docker image prune -f`.
- **Обновить всё:** последовательно `configuration → identity → users → files` (web исключён).
- **restart / start / stop:** `docker <action> <container>` по белому списку (web запрещён — только self-методы).
- **Self-update / self-restart веба:** detached **helper-контейнер** из образа самого web (`docker run -d --rm` с `sh -c "sleep 2 && docker compose … pull web && … up --force-recreate -d web"`). Веб не может пересоздать себя изнутри. Host-пути для `-v` берутся через `docker inspect` mount'ов `cloud-web`; имя compose-проекта — из метки `com.docker.compose.project`.
- Креды приватного registry: `DOCKER_CONFIG=/root/.docker`; на хосте нужен `docker login docker.barkfluff.com:5000`, его `~/.docker/config.json` смонтирован в `cloud-web`.

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
- `docker-compose.yml` сервис `web`: `user: root`, volumes `docker.sock`/`./docker-compose.yml`/`./.env`/`~/.docker/config.json`, env `App__AdminPassword`.
- `sample.env`: `WEB_ADMIN_PASSWORD`.

## Компромисс безопасности

В BarkFluff это был изолированный admin-сервис; здесь те же права (`docker.sock`+root → полный контроль над хостом) получает публичный веб. Митигация: отдельный пароль + подписанная cookie, белый список из 5 сервисов, веб себя не stop/start (только self-update/-restart).
