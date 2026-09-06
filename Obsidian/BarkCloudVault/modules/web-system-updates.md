# Backend — Web: Обслуживание (обновление бэкенда)

Parent: [[modules/backend-web]] · See also: [[index]] · [[structure/infrastructure]]

## Назначение

Раздел «Обслуживание» в настройках (`/settings`) управляет контейнерами BarkCloud на одном
локальном Docker-хосте. Логика адаптирована из админ-панели BarkFluff: без SSH и удалённых
серверов, но с серверной очередью, проверкой состояния контейнеров и отдельными страницами
ожидания для пересоздания web.

## Доступ

В BarkCloud нет ролей. Раздел закрыт отдельным паролем `App:AdminPassword`
(`WEB_ADMIN_PASSWORD`). `Auth/AdminGate.cs` выдаёт HttpOnly-cookie `bark_admin`, подписанную
HMAC-SHA256 на общем `JwtSettings:SecretKey`, сроком 30 минут. Все `/api/system/*`, кроме
`unlock`, требуют авторизованного пользователя и разблокированного раздела.

## Управляемый набор

Инфраструктуру postgres/minio/rabbitmq/seq/nginx не трогаем. Белый список приложения.
В UI и очереди используются короткие имена, а Compose — ключи с префиксом `cloud-`:

`configuration→cloud-configuration→cloud-configuration`,
`identity→cloud-identity→cloud-identity`, `users→cloud-users→cloud-users`,
`files→cloud-files→cloud-files`, `notification→cloud-notification→cloud-notification`,
`torrent→cloud-torrent→cloud-torrent`, `web→cloud-web→cloud-web`.

Отсутствующие optional-сервисы (`notification`, `torrent`) показываются как «не найден» и
исключаются из массовой операции. Web не запускается через обычный `start/stop/restart`.

## Серверная очередь

`Infrastructure/DeploymentJobService.cs` — singleton `BackgroundService` с одним consumer:
одновременно выполняется только одна Docker-операция. Задача хранит шаги, состояние,
сообщения и признак отката; последние 20 завершённых задач живут в памяти процесса.

- Для одного сервиса доступны операции update/restart/start/stop.
- Массовые update/restart сначала читает фактически существующие контейнеры, затем ставит
  их в очередь в порядке `configuration → identity → users → files → notification → torrent`.
- Каждый update делает `pull` и `up --force-recreate --no-deps`, ждёт состояние контейнера.
  `running + healthy` считается успехом; без healthcheck нужны два опроса `running` подряд.
  `exited/dead/restarting/unhealthy` — явная ошибка, после которой старый image ID
  перемаркируется под прежнюю ссылку и сервис пересоздаётся. Таймаут не трактуется как
  доказательство поломки и автоматически не откатывается.
- `docker image prune -f` выполняется только после всей update-задачи, когда материал для
  отката больше не нужен.

`DockerService` запускает `docker compose` через helper-контейнер из образа web. Helper
получает реальный host-каталог compose, compose-файл, `.env` и `docker.sock` из `docker inspect`;
это сохраняет корректный резолв относительных bind mounts под Linux/WSL и Windows Docker
Desktop. Аргументы CLI передаются через `ProcessStartInfo.ArgumentList`.

## Self-update веба

Web нельзя пересоздать из собственного процесса. `UpdateWebSelfAsync` и
`RestartWebSelfAsync` запускают detached helper из текущего образа web. Self-update клонирует
текущую конфигурацию `cloud-web` (env, labels, ports, mounts, networks), делает `pull`,
переименовывает старый контейнер в `cloud-web-bak`, запускает новый и при ошибке `docker run`
или подключения сети возвращает старый контейнер. Self-restart выполняет отложенный
`docker restart cloud-web`.

После ответа API браузер открывает `/updating` или `/restarting`. Эти анонимные страницы
подставляют метку текущего процесса, опрашивают `/healthz` каждые 3 секунды и возвращаются в
`/settings#system` только после трёх успешных ответов уже нового процесса. Cache-Control
`no-store` и query-параметр при возврате не дают браузеру застрять на старом SPA-бандле.

## Эндпоинты (`SystemEndpoints.cs`)

- `GET /healthz` — анонимный health-check с заголовком `X-BarkCloud-Started-At`;
- `/updating`, `/restarting`, `/maintenance-wait.js` — страницы и скрипт ожидания;
- `POST /api/system/unlock`, `POST /api/system/lock`, `GET /api/system/services`;
- `POST /api/system/services/{svc}/{update|restart|start|stop}` — ставит одну операцию в очередь;
- `POST /api/system/update-all`, `POST /api/system/restart-all`;
- `GET /api/system/deploy/jobs`, `GET /api/system/deploy/jobs/{id}` — список и состояние задач;
- `POST /api/system/web/update-self`, `POST /api/system/web/restart-self`.

## UI

`ClientApp/src/pages/SettingsPage.tsx` (`SystemSection`) показывает статус, image tag и
действия. Модалка прогресса опрашивает серверную задачу каждые 2 секунды, показывает шаги,
ошибки и откат, а при повторном открытии раздела продолжает активную задачу. Массовые
кнопки больше не выполняют цикл запросов из браузера. Self-update/-restart переводит на
страницу ожидания. Отсутствующие контейнеры и параллельные действия блокируются в UI.

## Инфраструктура

- `BarkCloud.Web/Dockerfile` и `Dockerfile.slim`: `aspnet:10.0-alpine` + `docker-cli`,
  `docker-cli-compose`, `icu-libs`, `tzdata`; root задаётся compose для доступа к socket.
- `docker-compose.yml` у web монтирует `docker.sock`, `./docker-compose.yml` и `./.env`;
  `App__AdminPassword` получает `WEB_ADMIN_PASSWORD`.
- Registry публичен на pull; `DOCKER_CONFIG=/tmp/barkcloud-docker` не читает host
  `credsStore`, отсутствующий внутри alpine-образа.

## Компромисс безопасности

`docker.sock` и root дают web полный контроль над Docker-хостом. Митигация: отдельный пароль,
подписанная cookie и белый список из шести backend-сервисов; web сам управляется только
ограниченными helper-сценариями update/restart.
