# Web — миграция S3

Parent: [[Web/WebApp]] · See also: [[Web/Settings]] · [[Backend/Configuration]] · [[Backend/Files]]

## Назначение

Административный поток копирует объекты из существующего S3-профиля в отдельное назначение, затем переносит связанные профили Configuration на новое подключение. Исходные объекты копируются; очистки исходного бакета нет.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Web/StorageMigrationEndpoints.cs` | `MapStorageMigrationEndpoints` | HTTP API миграции |
| `Backend/BarkCloud.Web/Infrastructure/StorageMigrationService.cs` | `StorageMigrationService` | Проверки, задачи копирования и переключение |
| `Backend/BarkCloud.Web/Infrastructure/S3MigrationCopier.cs` | `S3MigrationCopier` | Доступ к бакетам, копирование и проверка объектов |
| `Backend/BarkCloud.Web/Infrastructure/StorageMigrationControl.cs` | `StorageMigrationControl` | Изменения Configuration и долговечный барьер Files |
| `Backend/BarkCloud.Web/ClientApp/src/pages/MigrationTab.tsx` | `MigrationTab` | UI копирования и прогресса |

## Публичные контракты

Все маршруты требуют пользовательскую сессию и открытую AdminGate.

| Метод и путь | Поведение |
|---|---|
| `GET /api/settings/migration/sources` | Список источников, сгруппированных по используемому бакету |
| `POST /api/settings/migration/check` | Принимает `{sourceId, destination}`; destination содержит `serviceUrl`, `bucketName`, `accessKey`, `secretKey`, `isR2`, `region` и `forcePathStyle`. Возвращает validation id |
| `POST /api/settings/migration/start` | Принимает `{validationId}` и запускает копирование по проверенному подключению |
| `GET /api/settings/migration/jobs`, `GET /api/settings/migration/jobs/{id}` | Сводки и прогресс задач |
| `POST /api/settings/migration/jobs/{id}/retry`, `POST /api/settings/migration/jobs/{id}/cancel` | Продолжение задачи после ошибки или безопасная отмена |
| `POST /api/settings/server/storage/migration/apply` | Принимает `{jobId}` и применяет переключение профилей после успешного копирования |
| `GET /api/settings/migration/cutovers`, `POST /api/settings/migration/cutovers/{id}/cancel`, `POST /api/settings/migration/cutovers/{id}/recover` | Просмотр, безопасная отмена или восстановление переключения Files |

Сводка задачи содержит состояние (`queued`, `running`, `stopping`, `copied`, `failed`, `cancelled` или `completed`), фазу, объёмы, текущий объект, безопасную ошибку, число активных загрузок и флаги доступных действий. `totalBytes`, `copiedBytes` и `currentBytes` передаются десятичными строками; также доступны счётчики пропущенных и загруженных объектов и причина повторного копирования.

## Зависимости и взаимодействия

1. `check` отклоняет бакет, совпадающий с источником или уже используемый профилем BarkCloud. Доступность назначения проверяется чтением списка и временным объектом: запись, чтение, проверка SHA-256 и удаление. Проверка действует 15 минут и связана с пользователем.
2. `start` потребляет проверку; одновременно выполняется одна задача. Копирование сверяет существующие объекты с источником: совпавшие подтверждает без повторной записи, отсутствующие или отличающиеся копирует с проверкой SHA-256 и переносом S3 metadata/headers.
3. `apply` сначала проверяет возможность перезапуска Files. Затем Files ставит долговечный барьер, ждёт активные загрузки, Web выполняет финальную синхронизацию, Configuration переносит профили одной операцией API, после чего Files перезапускается и проверяет барьер.
4. Состояние cutover хранит Files. Если Web перезапустился во время переключения, список `cutovers` показывает оставшийся барьер; `recover` повторяет применение и проверку Files.

## Ограничения и важные детали

- Web-задачи и проверенные S3-подключения хранятся в памяти процесса и не восстанавливаются после перезапуска Web. Пароли S3 не сохраняются на диск; незавершённую задачу копирования после перезапуска начинают заново через `check` и `start`.
- Источник используется только для чтения. Копирование не удаляет объекты ни при обычном завершении, ни после переключения профилей.
- Финальное переключение нельзя отменить после начала записи новой конфигурации; в этом состоянии безопасное действие — завершить или восстановить применение.

## Решения и основания

Между Web и Files используется долговечный барьер загрузок: `StorageMigrationControl` начинает cutover, ждёт состояния `frozen/applying`, а затем подтверждает `applied` после перезапуска Files. Это сохраняет безопасный порядок финального копирования и переключения профилей.
