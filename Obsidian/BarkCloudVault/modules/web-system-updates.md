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
исключаются из массовой операции. При отсутствии обязательного Compose-сервиса задача
останавливается до первого пересоздания. Web не запускается через обычный `start/stop/restart`.

## Серверная очередь

`Infrastructure/DeploymentJobService.cs` — singleton `BackgroundService` с одним consumer:
одновременно выполняется только одна Docker-операция. Задача хранит шаги, состояние,
сообщения и признак отката; последние 20 завершённых задач живут в памяти процесса.

- Для одного сервиса доступны операции update/restart/start/stop.
- Массовые update/restart строят цель по сервисам из `docker compose config --services`, а не
  по найденным контейнерам. Порядок: `configuration → identity → users → files → notification →
  torrent → web`; отсутствующий контейнер существующего Compose-сервиса будет создан,
  optional-сервис без Compose-описания пропускается.
- До первого пересоздания выполняется единый preflight: Docker daemon, helper-образ, CLI/Compose,
  mount-пути и имя Compose-проекта. Для update все образы скачиваются одной командой
  `docker compose pull`; одинаковая ошибка не размножается по шагам.
- Каждый update делает `up --force-recreate --no-deps`, ждёт состояние контейнера.
  `running + healthy` считается успехом; без healthcheck нужны два опроса `running` подряд.
  `exited/dead/restarting/unhealthy` — явная ошибка, после которой старый image ID
  перемаркируется под прежнюю ссылку и сервис пересоздаётся. Таймаут не трактуется как
  доказательство поломки и автоматически не откатывается.
- При ошибке preflight все шаги получают `Skipped`, а диагностика хранится только на задаче,
  чтобы одно и то же сообщение не дублировалось на каждом контейнере; при падении
  `configuration` зависимые шаги также получают `Skipped`. `docker image prune -f` выполняется
  только после полностью успешной update/switch-branch задачи, когда материал для отката больше
  не нужен.

`DockerService` запускает `docker compose` через helper-контейнер из образа web. Helper
получает реальный host-каталог compose, compose-файл, `.env` и `docker.sock` из `docker inspect`;
это сохраняет корректный резолв относительных bind mounts под Linux/WSL и Windows Docker
Desktop. Аргументы CLI передаются через `ProcessStartInfo.ArgumentList`.

## Self-update веба

Web нельзя пересоздать из собственного процесса. `UpdateWebSelfAsync` и
`RestartWebSelfAsync` запускают detached helper из текущего образа web. Self-update клонирует
текущую конфигурацию `cloud-web` (env, labels, ports, mounts, networks), делает `pull`,
переименовывает старый контейнер в `cloud-web-bak`, запускает новый и при ошибке `docker run`
или подключения сети возвращает старый контейнер. После запуска helper ждёт новый контейнер и
проверяет `running + healthy` либо два стабильных `running` без healthcheck; при crash-loop,
`unhealthy` или таймауте выполняется rollback. Self-restart выполняет отложенный
`docker restart cloud-web`.

Helper пишет `last-operation.json` и лог в persistent volume `/app/maintenance`. При первом
self-update старой установки helper сам подключает volume с именем `<compose-project>_cloud-web-maintenance`
и переводит Compose mount в `rw`, поэтому миграция не теряет результат операции. Для переключения
канала web в этот volume также сохраняется резервная копия Compose, поэтому после перезапуска
процесса видны ошибка, команда/exit-код/stderr и факт rollback. Compose-файл web монтируется с
`rw`, запись идёт в тот же inode.

После ответа API браузер открывает `/updating` или `/restarting`. Эти анонимные страницы
подставляют метку процесса до операции, идентификатор detached-helper и опрашивают
`/healthz` каждые 3 секунды. Идентификатор проверяется через `/maintenance-status`: три
успешных ответа нового процесса либо завершённый именно этой операцией helper возвращают
браузер в `/settings#system`; состояние `failed` сразу показывает сообщение и ссылку на
защищённую диагностику. Cache-Control `no-store`, непрозрачный operation ID и query-параметры
не дают браузеру застрять на свежей странице с тем же timestamp.

## Эндпоинты (`SystemEndpoints.cs`)

- `GET /healthz` — анонимный health-check с заголовком `X-BarkCloud-Started-At`;
- `GET /maintenance-status?operationId=` — анонимное минимальное состояние конкретной
  detached-операции (`pending`, `completed`, `failed`) без helper-диагностики;
- `/updating`, `/restarting`, `/maintenance-wait.js` — страницы и скрипт ожидания;
- `POST /api/system/unlock`, `POST /api/system/lock`, `GET /api/system/services` (статус,
  канал, current/latest SemVer, updateAvailable, versionState/versionError);
- `GET /api/system/branches`, `POST /api/system/services/{svc}/branch` с `{ branch }`;
- `POST /api/system/services/{svc}/{update|restart|start|stop}` — ставит одну операцию в очередь;
- `POST /api/system/update-available`, `POST /api/system/update-all`,
  `POST /api/system/restart-all`;
- `GET /api/system/deploy/jobs`, `GET /api/system/deploy/jobs/{id}` — список и состояние задач
  с `Pending/InProgress/Completed/Failed/Skipped`, диагностикой, rollback и `requiresReconnect`;
- `POST /api/system/web/update-self`, `POST /api/system/web/restart-self` возвращают
  `operationId`, который передаётся странице ожидания вместе с timestamp до операции.

При чтении статуса Web поле `docker ps .Image` может содержать короткий ID образа вместо
registry-ссылки. В этом случае `DockerRegistryService` использует reference из Compose и
получает digest по каноническому репозиторию, поэтому SemVer и канал остаются доступны
даже для контейнеров, созданных по ID или digest. Registry-ответы в формате OCI index
дополнительно раскрываются до платформенного manifest и его `config.digest`: локальный
Docker может не сохранять `RepoDigests`, а `.Image` тогда содержит config ID или его
сокращённый префикс. Если совпадение уже найдено по index/child digest, дополнительный
запрос за config не требуется.

## UI

`ClientApp/src/pages/SettingsPage.tsx` (`SystemSection`) показывает адаптивную таблицу/карточки:
статус, канал, текущую и последнюю SemVer, badge доступного обновления, действия и раскрываемую
техническую ошибку. Кнопки — «Обновить доступные (N)», «Обновить все», «Перезапустить все» и
«Обновить статус». Модалка прогресса опрашивает серверную задачу каждые 2 секунды, показывает
skipped-шаги, команду/stderr и rollback, а при `requiresReconnect` переводит на `/updating` или
`/restarting`.

## Инфраструктура

- `BarkCloud.Web/Dockerfile` и `Dockerfile.slim`: `aspnet:10.0-alpine` + `docker-cli`,
  `docker-cli-compose`, `icu-libs`, `tzdata`; root задаётся compose для доступа к socket.
- `docker-compose.yml` у web монтирует `docker.sock`, `./docker-compose.yml:rw`, `./.env` и
  named volume `cloud-web-maintenance:/app/maintenance`; там лежат compose-backups и marker
  self-update.
  `App__AdminPassword` получает `WEB_ADMIN_PASSWORD`.
- Web подключает `AddBarkCloudSerilog("BarkCloud.Web")`: события очереди и ошибки самого
  процесса дублируются в stdout контейнера и Seq, поэтому их можно смотреть через
  `docker logs`; подробный вывод detached helper сохраняется в persistent maintenance-логе
  и показывается в защищённом разделе обслуживания. Внешний проброс порта Seq не требуется.
- Registry публичен на pull; `DOCKER_CONFIG=/tmp/barkcloud-docker` не читает host
  `credsStore`, отсутствующий внутри alpine-образа.

## Компромисс безопасности

`docker.sock` и root дают web полный контроль над Docker-хостом. Митигация: отдельный пароль,
подписанная cookie и белый список шести микросервисов плюс web; web сам управляется только
ограниченными helper-сценариями update/restart.
