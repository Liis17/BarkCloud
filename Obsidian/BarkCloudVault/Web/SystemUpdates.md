# Web — обслуживание контейнеров

Parent: [[Web/WebApp]] · See also: [[Platform/Infrastructure]] · [[Web/Settings]]

## Назначение

Раздел обслуживания управляет Docker-контейнерами приложения на хосте, где запущен Web. Изменения проходят через серверную очередь; браузер получает статус задачи и не выполняет Docker-команды.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Web/SystemEndpoints.cs` | `MapSystemEndpoints` | HTTP API, health-check и страницы ожидания |
| `Backend/BarkCloud.Web/Auth/AdminGate.cs` | `AdminGate` | Проверка дополнительного пароля |
| `Backend/BarkCloud.Web/Infrastructure/AdminUnlockLimiter.cs` | `AdminUnlockLimiter` | Ограничение попыток разблокировки |
| `Backend/BarkCloud.Web/Infrastructure/DeploymentJobService.cs` | `DeploymentJobService` | Последовательная очередь развёртывания |
| `Backend/BarkCloud.Web/Infrastructure/DockerService.cs` | `DockerService` | Вызовы Docker и Compose |
| `Backend/BarkCloud.Web/Infrastructure/MaintenanceOperationStore.cs` | `MaintenanceOperationStore` | Состояние detached-операций Web |

## Публичные контракты

| Метод и путь | Поведение |
|---|---|
| `POST /api/system/unlock`, `POST /api/system/lock` | Открыть или закрыть AdminGate |
| `GET /api/system/services`, `GET /api/system/branches` | Статус контейнеров, образов и доступных каналов. `includeVersions=false` пропускает проверку версий образов в реестре |
| `POST /api/system/services/{service}/{action}` | Операция над сервисом; `action` — `update`, `restart`, `start` или `stop` |
| `POST /api/system/services/{service}/branch` | Переключить канал образа сервиса |
| `POST /api/system/update-available`, `POST /api/system/update-all`, `POST /api/system/restart-all` | Массовые операции |
| `GET /api/system/deploy/jobs`, `GET /api/system/deploy/jobs/{id}` | Список и состояние задач |
| `POST /api/system/web/update-self`, `POST /api/system/web/restart-self` | Обновить или перезапустить Web через отдельный helper; ответ содержит operation id |
| `GET /healthz`, `GET /maintenance-status?operationId=…`, `/updating`, `/restarting` | Проверка доступности и страницы ожидания self-update/restart |

## Зависимости и взаимодействия

- Для разблокировки нужны обычная пользовательская сессия и пароль `App:AdminPassword` (в compose приходит из `WEB_ADMIN_PASSWORD`). Успех выдаёт HttpOnly-cookie `bark_admin` сроком 30 минут, подписанную HMAC-SHA256 общим JWT-секретом.
- На попытки разблокировки действует лимит: до 5 попыток за 15 минут на IP и до 20 за час суммарно; счётчики хранятся в памяти Web.
- Очередь имеет одного обработчика и упорядочивает массовое обновление: Configuration, Identity, Users, Files, Notification, Torrent, затем Web.
- Управляемые сервисы приложения: `configuration`, `identity`, `users`, `files`, `notification`, `torrent` и `web`. PostgreSQL, MinIO, RabbitMQ, Seq и reverse proxy в этот список не входят.
- `Docker:ContainerNames:<service>` в конфигурации Web задаёт фактическое имя контейнера для обслуживания. Если параметр не задан, сохраняется прежнее имя `cloud-<service>`. Ключи Compose и DNS-алиасы `cloud-*` остаются фиксированными; имена проверяются на допустимый формат, дубли и конфликты с DNS-алиасами приложения и инфраструктуры.
- Для доступа к Docker Web запускается с `docker.sock` и правами root. Compose хранит файл операции и журнал helper в томе `cloud-web-maintenance`; подключение задаётся в [[Platform/Infrastructure]].

## Ограничения и важные детали

Web нельзя пересоздать из обслуживающего HTTP-запроса: для self-update и self-restart запускается detached helper. Страница ожидания проверяет состояние конкретной операции через operation id и доступность нового процесса.

Self-update создаёт временные имена helper и backup из имени Web и уникального operation id. Если одно из имён уже занято, операция завершается без удаления существующего контейнера. Rollback удаляет новый Web только после проверки operation-label и ID контейнера; backup сверяется с ID исходного Web. Скрипты обслуживания не перезапускают и не перечитывают nginx: внешний reverse proxy может обслуживать несколько проектов, а восстановление backend-адресов выполняется через DNS Docker. Для первого перехода со статической конфигурации nginx нужно вручную применить динамический upstream-конфиг и убедиться, что nginx подключён к общей Docker-сети; текущий helper больше не выполняет reload.

При self-update новый Web и контейнер rollback сохраняют из `docker inspect` флаги и параметры запуска, связанные с завершением процесса: `HostConfig.Init`, `Config.StopTimeout` и `Config.StopSignal`, если они заданы. Остальные переменные окружения и mounts также копируются, в том числе настройки и spool Lognara agent. Старый Web останавливается без явного timeout, поэтому Docker применяет его `Config.StopTimeout` или настройку daemon; self-restart также не переопределяет timeout контейнера.

Причина отдельного AdminGate — в комментарии `AdminGate`: для операций Docker Web использует дополнительный пароль и подписанную cookie поверх пользовательской сессии.
