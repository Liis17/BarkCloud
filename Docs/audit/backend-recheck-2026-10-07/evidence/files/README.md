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

F20 исправлен. До исправления новая регрессия завершалась `Expected: "name"; Actual: ""`; после исправления `Search_FolderMatchField_UsesSqlMatchSemantics` проходит и возвращает `match_field=name`, `match_value=report`, при SQL `word_similarity=1`. Матрица `UnifiedSearchMatchPostgresTests`: **11 passed, 0 failed, 0 skipped**. Полный Files-набор с `BARKCLOUD_TEST_POSTGRES`: **633 passed, 0 failed, 0 skipped**; portable `Recheck.csproj --filter FullyQualifiedName~F20`: **1 passed, 0 failed, 0 skipped**. PostgreSQL-тесты действительно выполнялись на одноразовом PostgreSQL 18.6.

### Замер EXPLAIN F20

SQL страницы получен через `ToQueryString()` у настоящих запросов `UnifiedSearchService.Search` на базе `4cb54d97` и HEAD `f5b6a8c3`. Для всех десяти запросов SQL без комментариев параметров совпал с командой, отправленной EF PostgreSQL. Каждый сценарий — запрос `bulk`, `limit=20` (внутренний `LIMIT 21`); один прогревочный и пять измеряемых `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`. В таблице — медиана времени выполнения и буферов корневого узла; buffers приведены как shared hit/read blocks.

Условия: PostgreSQL 18.6 Homebrew (`/opt/homebrew/opt/postgresql@18/bin`), локаль `C.UTF-8`, одноразовый кластер `/tmp/bc-pg-f20-explain`, порт `55431`, `pg_trgm` включён миграциями Files. Каталог — fixture `SearchCatalogFixture` с 2 000 bulk-фото плюс по 2 000 синтетических треков, документов, удалённых документов и альбомов; все синтетические треки и документы имеют теги и metadata. Это тестовый каталог, не бенчмарк продакшена.

| Секция | До: медиана, мс; hit/read | После: медиана, мс; hit/read | Основные узлы до → после |
|---|---:|---:|---|
| Photos | 5,77; 6 625/0 | 6,30; 7 342/0 | `Limit → Sort → Nested Loop` → `Incremental Sort → Subquery Scan → Limit → Nested Loop` |
| Files | 20,79; 1 639/0 | 19,33; 3 983/0 | `Limit → Sort → Nested Loop → Aggregate → Append` → `Incremental Sort → Subquery Scan → Limit → Nested Loop` |
| Tracks | 15,04; 1 525/0 | 15,42; 3 869/0 | `Limit → Sort → Nested Loop → Aggregate → Append` → `Incremental Sort → Subquery Scan → Limit → Nested Loop` |
| Trash | 29,74; 17 252/0 | 25,68; 22 739/0 | `Limit → Sort → Nested Loop → Aggregate → Append` → `Incremental Sort → Subquery Scan → Limit → Nested Loop` |
| Albums | 2,98; 29/0 | 4,29; 339/0 | `Limit → Sort → Seq Scan` → `Subquery Scan → Limit → Hash Join → Append` |

Фрагменты фактического SQL Albums показывают внутренний `LIMIT 21` и выбор подписи по тем же `Rank`/`Sim` после страницы:

```sql
WHERE u1."Key" = s."Id"
  AND (u1."Rank" = s."Rank" OR (u1."Rank" IS NULL AND s."Rank" IS NULL))
  AND (u1."Sim" = s."Sim" OR (u1."Sim" IS NULL AND s."Sim" IS NULL))
ORDER BY u1."Order", u1."Value" COLLATE "C"
LIMIT 1

FROM (
    ...
    ORDER BY u0."Rank" DESC, u0."Sim" DESC, a."UpdatedAt" DESC, a."Id" DESC
    LIMIT @p -- @p = 21
) AS s
```

В плане HEAD подписи `MatchField` и `MatchValue` вычисляют два коррелированных `SubPlan Limit` после внутреннего `LIMIT 21`; для каждой подписи `Actual Loops=21`. План ранжирует SQL-кандидатов для совпавших записей, но не возвращает и не материализует весь каталог в приложении. Чтение блоков после прогрева не потребовалось: все замеренные `Shared Read Blocks` равны нулю. У Albums самый большой относительный рост — 2,98 → 4,29 мс (+44%, +310 shared hit blocks); это около 1,31 мс на данном тестовом каталоге. Многократного роста времени на расширенном каталоге не обнаружено.
