# Files: доказательства повторной проверки

Исходный срез от 2026-10-07 ниже сохраняет результаты воспроизведения до исправлений.

Проверка 2026-10-07 на `d6563a2`, .NET SDK 10.0.203, PostgreSQL 18.6, `C.UTF-8`. Production-код и tracked тесты не изменялись. Все SQL-тесты создают отдельные БД с актуальными миграциями через существующий `PostgresFilesDatabase` и удаляют их в конце. Изолированный локальный кластер остановлен после проверки. S3 в F15 заменён mock; физический MinIO не проверен.

- [results.txt](results.txt): **564 passed, 0 failed, 0 skipped**, полный актуальный Files.
- [files-residuals.xml](files-residuals.xml) (TRX), [repro.txt](repro.txt): **3 failed / 3 total**, проверки ожидаемого корректного поведения воспроизводят F13/F15/F20. Ожидается exit code 1 до исправления.
- [ResidualFilesTests.cs](ResidualFilesTests.cs), [Recheck.csproj](Recheck.csproj): переносимый исходник дополнительных проверок. ProjectReference/Compile используют пути относительно репозитория; строки подключения в файлах отсутствуют.

## Обновление F15, 2026-10-08

F15 исправлен. Его диагностический исходник адаптирован к общей границе: purge ждёт Attach, ожидание подтверждается `pg_locks`, затем барьер отпускается. Результат: `Entries=1`, `Blobs=0`, `deletedKeys=[]`, `waited=true`, `attachSucceeded=true`, `blobExists=true`, `liveEntries=1`; F15 проходит. Полный штатный Files-набор на рабочем дереве с F13/F15 — **619 passed, 0 failed, 0 skipped**. Проверено на PostgreSQL 18.6, `C.UTF-8`; S3 mock. Сохранённые результаты аудита 2026-10-07 ниже остаются историческими. Подробности — [F15.md](../../F15.md).

## Повторный запуск

Запусти отдельный тестовый PostgreSQL и задай `BARKCLOUD_TEST_POSTGRES` через своё окружение. Учётная запись должна создавать и удалять временные БД. Приложение/очередь/S3 запускать не требуется.

Из корня репозитория (артефакты можно направить в любой внешний каталог):

```sh
dotnet test Tests/Backend/BarkCloud.Files.Tests/BarkCloud.Files.Tests.csproj --artifacts-path /tmp/barkcloud-files-tests-artifacts
dotnet test Docs/audit/backend-recheck-2026-10-07/evidence/files/Recheck.csproj --artifacts-path /tmp/barkcloud-files-repro-artifacts --logger 'console;verbosity=detailed'
```

Для одного пункта добавь `--filter FullyQualifiedName~F13` (либо F15/F20). Барьеры синхронизируют порядок запросов без произвольных задержек; safety timeout 30 секунд выявляет зависание теста.

## Проверенный результат

| Проверка | Наблюдение |
|---|---|
| F13: rename остановлен перед SaveChanges; DeleteDirectory завершён; rename продолжен | Папка отсутствует, запись живая, даты корзины null |
| F15: Attach остановлен перед INSERT; purge завершён; Attach продолжен | Purge удалил 1 запись/1 блоб и вызвал mock S3 Delete; Attach успешен, 1 живая запись, оригинала нет |
| F20: Search папки report по quarterly report finances | Хит есть, MatchField/MatchValue пусты; SQL-score 1, настоящий C#-score 0.36363636363636365 |

В сохранённых логах и TRX временный абсолютный путь заменён на `<repro>`. Остальные сообщения и результаты сохранены. Исходная внешняя копия и полный build/test log: `/tmp/barkcloud-files-recheck.cqwEo7/`.

## Обновление F20, 2026-10-10

F20 исправлен. До исправления новая регрессия завершалась `Expected: "name"; Actual: ""`; после исправления `Search_FolderMatchField_UsesSqlMatchSemantics` проходит и возвращает `match_field=name`, `match_value=report`, при SQL `word_similarity=1`. Матрица `UnifiedSearchMatchPostgresTests`: **9 passed, 0 failed, 0 skipped**. Полный Files-набор с `BARKCLOUD_TEST_POSTGRES`: **631 passed, 0 failed, 0 skipped**; portable `Recheck.csproj --filter FullyQualifiedName~F20`: **1 passed, 0 failed, 0 skipped**. PostgreSQL-тесты действительно выполнялись на одноразовом PostgreSQL 18.6.
