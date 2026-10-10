# Повторная проверка backend — 2026-10-10

Срез `2e1a63c3eb5384bfff94c093f68ba973e8a4a6ea`, ветка `master`. Проверены все 12 пунктов предыдущего отчёта. Production-код, штатные тесты и workflows не изменялись.

## Итог

| Пункт | Статус | Основание |
|---|---|---|
| F02 | Выполнен | Исправление `568e42b`; точная PostgreSQL-регрессия stale refresh после reset проходит. |
| F13 | Выполнен | Исправление `9310126e`: чтение под LockTree, отказ для корзины, сохранение изменённых полей; 24 конкурентные регрессии проходят. |
| F15 | Выполнен | Исправление `9310126e`: общие замки, повторное чтение purge, проверка ссылок, FK Restrict; 19 конкурентных и 3 миграционных теста проходят. |
| F17 | Выполнен | Исправление `50ffa20a`: два часа для передачи, RequestAborted в Web; реальные передачи дольше 100 секунд и отмена проходят. |
| F18 | Не выполнен | Конфигурация retry сохраняет 1270 секунд ожиданий внутри двух consumer-слотов; in-memory probe блокирует исправное сообщение. |
| F19 | Не выполнен, оба подэтапа | Отдельный enqueue теряет уведомление при отказе INSERT; RegisterDevice по-прежнему без deadline/token. Оба сценария воспроизведены. |
| F20 | Не выполнен | Реальный PostgreSQL Search возвращает `report`, но MatchField пуст; диагностический тест падает на ожидаемом `name`. |
| F21 | Не выполнен | После отказа save следующий flush сохраняет 105/207 вместо 115/227; baseline сдвигается до commit. |
| F22 | Не выполнен | При конкурентных Pause/Resume и отказе save действие движка расходится с Paused в БД. |
| F23 | Не выполнен | 16/31 байт проходят startup, но реальный signer падает с IDX10720; 32 байта подписывают и валидируют JWT. |
| F24 | Частично выполнен | GrpcServer добавлен в события/фильтры. Torrent.Tests и общие build-файлы отсутствуют; gate публикации допускает skipped. |
| Q03 | Не выполнен | CloudApiEndpoints — 1904 строки; самостоятельные сценарии и повторение claims остаются. Замечание по сопровождению. |

Закрытые пункты означают исправление в проверенном коде. Применение миграций и обновление развёрнутых сервисов здесь не проверялось.

## Штатные проверки

Запуск в Release на .NET 10. Для тестов PostgreSQL использован отдельный временный PostgreSQL 18.6 с правом создания тестовых БД, без обращения к рабочей БД проекта. Для длительных upload-тестов установлен `BARKCLOUD_LONG_UPLOAD_TEST=1`.

| Набор | Результат |
|---|---|
| Files.Tests, весь проект, PostgreSQL `C.UTF-8` | 619 прошли, 0 упало, 0 пропущено |
| Identity.IntegrationTests, весь проект | 25 прошли, 0 упало, 0 пропущено |
| Web.Tests, фильтр LegacyUploadEndpointsTests | 6 прошли, 0 упало, 0 пропущено |
| Torrent.Tests, весь проект | 31 прошёл, 0 упало, 0 пропущено |

Всего в перечисленных итоговых прогонах **681/681**, без пропусков. Это не полный прогон всего репозитория. Отдельный первый Files-прогон на locale `C` дал 618/619: Search_Folders_IncludesSystemDynamicFolders не нашёл обычную папку по кириллице. После пересоздания кластера с `C.UTF-8` полный набор прошёл; backend-код между прогонами не менялся.

Повторение: задать `BARKCLOUD_TEST_POSTGRES` для отдельного тестового PostgreSQL и `BARKCLOUD_LONG_UPLOAD_TEST=1`, затем выполнить:

```bash
dotnet test Tests/Backend/BarkCloud.Files.Tests/BarkCloud.Files.Tests.csproj -c Release
dotnet test Tests/Backend/BarkCloud.Identity.IntegrationTests/BarkCloud.Identity.IntegrationTests.csproj -c Release
dotnet test Tests/Backend/BarkCloud.Web.Tests/BarkCloud.Web.Tests.csproj -c Release --filter 'FullyQualifiedName~LegacyUploadEndpointsTests'
dotnet test Tests/Backend/BarkCloud.Torrent.Tests/BarkCloud.Torrent.Tests.csproj -c Release
```

## Остаточные дефекты и ограничения

Использованы [диагностические исходники предыдущей проверки](backend-recheck-2026-10-07/evidence/README.md), отдельно от штатных наборов:

- Identity probe: `bytes=16/31: startupAccepted=True; signing=ArgumentOutOfRangeException; IDX10720=True`; 32 байта — успешная подпись/валидация. RegisterDevice: `deadlinePresent=False; rpcTokenCanCancel=False; IssueAsyncCompleteAfterCallerCancel=False`; завершение только после ручного освобождения RPC. SetPassword на PostgreSQL с отказом INSERT: `handlerSuccess=true; newPasswordCommitted=True; PendingNotifications=0; enqueueFailed=1`.
- Files probe, только F20: один ожидаемо упавший тест, `Expected: "name"; Actual: ""`. Это отдельная проверка дефекта, не ошибка штатного Files-набора.
- Torrent probe на relational SQLite: F21 `DB=105/207, expected=115/227, lost=10/20`; F22 после Resume и позднего Pause commit `DB.Paused=True, engine Paused=False`; после отказа сохранения Pause `DB.Paused=False, engine Paused=True`.
- F18: из webqueue probe выделена проверка consumer/retry без устаревшего теста F17. С текущими интервалами MassTransit 8.5.2: `limit=2; waitSeconds=1270; healthyProcessedWithin1500ms=False`.

Identity/Torrent/queue probes завершаются с кодом 0 при воспроизведении дефекта. Это не доказательство исправности. Для F18 используется in-memory middleware; настоящий RabbitMQ не запускался. Для F22 действия движка управляются существующими protected hooks; peer-сеть не поднималась. Для F15 физическое удаление S3 проверено mock, MinIO не запускался. Для F02 проверен отзыв после загрузки feed, а не мгновенная синхронизация всех реплик. Долгие F17-тесты используют настоящий HTTP/multipart и upstream с ответом после 105 секунд; полный стек nginx/Files не поднимался.

F24 проверен статически: все семь build-backend workflows содержат GrpcServer в push.paths/runtime-paths; tests.yml учитывает его для существующих тестов потребителей. Directory.Build.props и Tests/Directory.Build.props не охвачены; Torrent.Tests отсутствует в tests.yml и параметрах build-backend-torrent. Отдельный manual-workflow удалён; workflow_dispatch текущего tests.yml также не включает Torrent. `backend-service-ci.yml` сохраняет `needs.test.result != 'failure'`, принимающее skipped. GitHub Actions и публикация образов не запускались.

Подробности открытых заданий сохранены в [report.md](../../report.md) и связанных файлах; F24 снабжён уточнением частичного выполнения. Исторические логи предыдущего среза не перезаписаны.
