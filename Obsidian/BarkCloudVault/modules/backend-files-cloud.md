# Backend — Files · Cloud (иерархия папок)

Parent: [[modules/backend-files]] · See also: [[api/files-api]]

## Назначение

NextCloud-подобная иерархия папок и файловых записей пользователя поверх существующего хранилища `UploadFile`. Папки (`CloudDirectory`) образуют дерево; записи (`CloudFileEntry`) ссылаются на реальные `UploadFile` и могут иметь своё отображаемое имя независимо от исходного `Filename`.

Корневая папка владельца **не материализуется** — представлена `ParentId == null` для директорий и синтетическим `Guid.Empty` для `CloudFileEntry.DirectoryId` (нужно для уникального индекса `(OwnerId, DirectoryId, Name)`).

**Инвариант «одна директория на файл»**: блоб владельца может быть привязан максимум к одной директории. Гарантируется уникальным индексом `CloudFileEntries(OwnerId, FileId)` и проверкой в `AttachFile` (`FileAlreadyAttachedException`). Альбомы — отдельный слой many-to-many ([[modules/backend-files]]).

**Корзина (soft-delete)**: удаление файла/папки не удаляет данные сразу, а помечает записи `CloudFileEntry` как `IsDeleted` с датами `DeletedAt`/`PurgeAt` (хранение 14 дней). Уникальные индексы — **частичные** (`WHERE IsDeleted = false`), поэтому запись в корзине не блокирует повторную загрузку файла/имени. Записи в корзине исключаются из иерархии, галереи и альбомов, но `Uploaders` сохраняются (квота не освобождается). Окончательная зачистка (БД + S3 + превью + альбомы + избранное + публичные ссылки) — `TrashPurgeService` + фоновый `TrashCleanupService` (раз в 6 ч); осиротевшие блобы (пустой `Uploaders`) добивает фоновый `OrphanBlobCleanupService`. **Гонка restore ↔ purge (F06)**: purge не доверяет выбранному снимку — первым делом в короткой транзакции условно удаляет строки (`IsDeleted`, для воркера ещё `PurgeAt <= now`) и работает только с реально удалёнными; запись, восстановленная раньше, остаётся нетронутой вместе с блобом и привязками. `RestoreFromTrash` после окончательного удаления (`DbUpdateConcurrencyException`) отвечает `FileEntryNotFound`, как и `DeleteFromTrash` по уже восстановленной записи. См. [[modules/backend-files]].

## Domain

`Backend/BarkCloud.Files/Domain/`

### CloudDirectory.cs
- `Guid Id`
- `long OwnerId` — владелец
- `Guid? ParentId` — `null` = корень владельца
- `string Name`
- `DateTime CreatedAt`, `DateTime UpdatedAt`

### CloudFileEntry.cs
- `Guid Id`
- `long OwnerId` — владелец записи
- `Guid DirectoryId` — папка, в которой лежит (`Guid.Empty` = корень)
- `Guid FileId` — ссылка на реальный `UploadFile`
- `string Name` — отображаемое имя записи (не меняет `UploadFile.Filename`)
- `DateTime CreatedAt`
- `bool IsDeleted` — запись в корзине (исключается из всех «живых» выборок и частичных уникальных индексов)
- `DateTime? DeletedAt` — когда перемещена в корзину
- `DateTime? PurgeAt` — когда будет удалена окончательно (`DeletedAt` + 14 дней)

## Persistence

`Backend/BarkCloud.Files/Persistence/CloudHierarchyStorage.cs`:
- Константа `RootDirectoryId = Guid.Empty` — синтетический корень для уникального индекса
- Методы доступа к `CloudDirectories` и `CloudFileEntries` (`Get*`, `*AsNoTracking`, и др.). «Живые» выборки (`ListFilesInDirectory`, `GetFileEntriesInDirectories`, `FileEntryNameExists`, `FileEntryExistsForFile`) фильтруют `!IsDeleted`
- Методы корзины: `GetTrashedEntry`, `ListTrashedPage`, `GetAllTrashedEntries`, `GetExpiredTrashedEntries` (для воркера), `GetEffectivelyTrashedFileIds` (для скрытия из галереи/альбомов)
- Подключён к `FilesContext` (`CloudDirectories`, `CloudFileEntries` DbSet'ы)
- `LockTree(ownerId)` → `ICloudTreeLock` — замок структуры дерева владельца (F13), см. ниже
- `GetFileEntry` возвращает отслеживаемую запись, включая корзину. `UpdateFileEntry` сохраняет только изменённые поля через change tracking, без `DbSet.Update`; запись должна быть получена тем же хранилищем в том же контексте. Rename меняет только `Name`, move — только `DirectoryId`.

Миграции: `Persistence/Migrations/20260518174041_AddCloudDirectories.cs`; `20260525213058_AddTrashToCloudFileEntries.cs` (поля корзины + частичные уникальные индексы `WHERE IsDeleted = false` + индекс по `PurgeAt WHERE IsDeleted = true`).

## Целостность оригинала (F15)

Attach, restore, `DeleteUserMedia` и DB-фаза purge используют общий owner/tree lock. После ожидания читается актуальное состояние; проверка последней ссылки и освобождение владельца происходят до освобождения замка. Связь `CloudFileEntry.FileId → UploadFile.Id` защищена FK с `Restrict`; миграция очищает старые записи без оригинала. Порядок блокировок, восстановление после ошибки S3, тесты и внедрение — [[modules/files-original-lifetime]].

## Целостность дерева (F13)

Дерево — это `CloudDirectory.ParentId` без внешнего ключа, поэтому циклы и «сирот» предотвращает только код.

- **Замок структуры.** `ICloudHierarchyStorage.LockTree(ownerId)` открывает транзакцию EF и на Npgsql берёт `pg_advisory_xact_lock(hashtextextended('cloud-tree:{ownerId}', 0))`. Ключ не совпадает с замком квоты (`StorageQuotaService`: `pg_advisory_xact_lock(ownerId)`), чтобы замки не мешали друг другу. На SQLite (юнит-тесты) берётся только транзакция. Возвращает `ICloudTreeLock` (`CommitAsync` + `DisposeAsync`; без коммита — откат и снятие замка).
- **Где используется:** `MoveDirectory` и `DeleteDirectory` (циклы и перемещение в удаляемое поддерево) и все писатели, которые **ссылаются** на папку: `CreateDirectory` (`ParentId`), `AttachFile` (`DirectoryId`, в том числе системная папка из `EnsureSystemDirectory` при `route_by_media_kind`), `MoveFileEntry`, `RestoreFromTrash` (возврат в исходную папку), `CreateFolderShare` (публичная ссылка), `ShareFolderWithUser` (грант). Порядок строгий: замок → чтение папок (отслеживаемые сущности EF иначе останутся устаревшими) → проверки → запись → `CommitAsync`; запись активности и лог — после commit. Параллельные «A в B» и «B в A» выполняются по очереди: второй видит `A.ParentId == B` и получает `CircularMove`. Писатель, опередивший `DeleteDirectory`, сохраняет ссылку, и удаление забирает её вместе с поддеревом (запись уходит в корзину, ссылка и грант снимаются); опоздавший писатель получает `DirectoryNotFound`. Без замка писатель успевал проверить папку, пропускал удаление и сохранял живую запись/папку/ссылку/грант на несуществующую папку.
- **Цена замка:** структурные операции одного владельца идут по очереди на время нескольких SQL-запросов (в замке нет обращений к S3); замки разных владельцев независимы.
- **Переименование и одиночное удаление (остаток F13, закрыт 2026-10-08).** `RenameFileEntry` и `DeleteFileEntry` также берут замок владельца до `GetFileEntry`; у rename проверка конфликта имени входит в ту же транзакцию. Порядок: замок → чтение → проверки → изменение отслеживаемой записи → сохранение → commit → лог и активность. Rename, опередивший удаление папки или файла, заканчивается первым; последующее удаление сохраняет имя и отправляет запись в корзину. Rename после удаления получает `FileEntryNotFound`. Rename и move сохраняют новое имя и новое расположение в обеих очередностях; конфликт имени проверяется в актуальной папке. Узкое сохранение дополнительно защищает `DirectoryId`, `IsDeleted`, `DeletedAt` и `PurgeAt` от записи устаревшего снимка.
- **Контракт живой записи.** Rename и move после проверки владельца отклоняют `IsDeleted` как `FileEntryNotFound`, даже если имя/расположение совпадает; сначала требуется `RestoreFromTrash`. Если строка физически исчезла после чтения, rename, move и `DeleteFileEntry` преобразуют `DbUpdateConcurrencyException` от сохранения в `FileEntryNotFound`. Другие ошибки и отмена не маскируются. При отказе, отмене и операции без изменений commit и активность отсутствуют; освобождение транзакции откатывает изменения и снимает замок.
- **Вне замка:** `RenameDirectory` и `EnsureSystemDirectory` — они не создают ссылок на чужую папку (сирот и цикл не порождают); уникальность имён и системных типов закреплена индексами F16 (см. ниже). `RenameDirectory` против `DeleteDirectory` может закончиться `DbUpdateConcurrencyException` вместо `DirectoryNotFound`. `DeleteUserMedia` и `CreateArchive` создают записи сразу в корзине — ссылка на удалённую папку для них штатна (restore вернёт файл в корень).
- **Защита обходов от повреждённых данных:** `GetSubtree` пропускает уже посещённые папки (конечен и отдаёт каждую папку один раз, в том числе при самопетле); на нём держатся `DeleteDirectory`, `CreateArchive`, `ResolveFolderShare`, `RevokeFolderShare`, `FolderGrantAccessService`. Подъём по предкам в `MoveDirectory` при повторной папке отвечает `CircularMoveException`; в `GetPath` — `DirectoryTreeCorruptedException` ([[modules/shared-exceptions]]). `CreateArchive.RelativeDirPath` уже был ограничен счётчиком.
- **Не сделано:** уже существующие циклы и сироты в БД не чинятся автоматически. FK на `ParentId`/`DirectoryId` нет: у записей корневой `Guid.Empty` синтетический, а записи корзины намеренно указывают на удалённые папки; FK потребовал бы отдельной миграции с проверкой существующих сирот (по образцу F16).
- **Диагностика (только чтение).** Циклы — запросом с рекурсивным CTE по `CloudDirectories`; цикл недостижим из корня, но виден по ID (поиск, гранты, публичные ссылки). Сироты, оставшиеся от гонок до F13:
  ```sql
  -- папки с несуществующим родителем
  SELECT d."Id", d."OwnerId", d."ParentId" FROM "CloudDirectories" d
  WHERE d."ParentId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "CloudDirectories" p WHERE p."Id" = d."ParentId");
  -- живые записи в несуществующей папке (корень — Guid.Empty)
  SELECT e."Id", e."OwnerId", e."DirectoryId" FROM "CloudFileEntries" e
  WHERE NOT e."IsDeleted" AND e."DirectoryId" <> '00000000-0000-0000-0000-000000000000'
    AND NOT EXISTS (SELECT 1 FROM "CloudDirectories" d WHERE d."Id" = e."DirectoryId");
  -- публичные ссылки и гранты на несуществующую папку
  SELECT s."Id", s."DirectoryId" FROM "FolderShareLinks" s WHERE NOT EXISTS (SELECT 1 FROM "CloudDirectories" d WHERE d."Id" = s."DirectoryId");
  SELECT g."Id", g."DirectoryId" FROM "DirectoryGrants" g WHERE NOT EXISTS (SELECT 1 FROM "CloudDirectories" d WHERE d."Id" = g."DirectoryId");
  ```

Тесты: юнит — `CloudHierarchyStorageTests` (`GetSubtree` на цикле/самопетле), `GetPathCommandHandlerTests`, `MoveDirectoryCommandHandlerTests` (порядок замок → чтение → запись → коммит, цикл выше нового родителя), `DeleteDirectoryCommandHandlerTests`; для остальных писателей — `{CreateDirectory,AttachFile,MoveFileEntry,RestoreFromTrash,CreateFolderShare,ShareFolderWithUser}CommandHandlerTests` (порядок «замок → проверка → запись → commit», без commit при отказе). `{RenameFileEntry,MoveFileEntry,DeleteFileEntry}CommandHandlerTests` закрепляют commit до активности, освобождение замка, отсутствие commit/активности при отказе и операции без изменений, контракт корзины и ошибку исчезновения строки. Интеграционные на PostgreSQL (хелпер `_Helpers/PostgresFilesDatabase`, переменная `BARKCLOUD_TEST_POSTGRES`, без неё пропускаются): `Persistence/CloudTreeConcurrencyPostgresTests` — Move/Move и Move/Delete, оба хендлера останавливаются на барьере после проверки и до записи; `Persistence/CloudTreeWriterRacePostgresTests` — по одному сценарию «писатель ‖ `DeleteDirectory`» для всех шести писателей (перехватчик `SaveChanges` останавливает писателя после проверки папки и до записи, затем стартует удаление; инвариант — нет подпапок, живых записей, ссылок и грантов на несуществующую папку). Без `LockTree` все эти тесты падают. В `CloudDirectoryUniquenessPostgresTests` хелпер `Create` пишет через `AddDirectory` мимо хендлера: хендлер теперь сериализует создания одного владельца, а там проверяются сами уникальные индексы. Для CI сервис PostgreSQL включён у Files в `tests.yml` и `backend-service-ci.yml`.

`Persistence/CloudFileEntryConcurrencyPostgresTests` — 24 регрессии остатка F13: rename против `DeleteDirectory`, move и `DeleteFileEntry` в обеих очередностях, конфликт имени после move, узкое сохранение обоих вызывающих мест `UpdateFileEntry`, физическое исчезновение строки для трёх операций, отмена при ожидании замка/перед сохранением и откат при ошибке сохранения/commit без активности. Барьер перед UPDATE управляется `TaskCompletionSource`; первый запрос отпускается после завершения конкурента либо наблюдения фактического ожидания advisory-замка через `pg_locks`, с защитным таймаутом 20 секунд. Результаты и точные даты корзины читаются свежим контекстом; откат и снятие замка проверяются также до освобождения контекста ошибочного запроса. До исправления тест rename → `DeleteDirectory` падал на `IsDeleted=false`. После исправления на PostgreSQL 18.6 полный Files: **597 passed, 0 failed, 0 skipped** (2026-10-08).

## Уникальность папок (F16)

`FilesContext` защищает папки тремя unique-индексами:
- `(OwnerId, ParentId, Name)` — прежний индекс имён вложенных папок.
- `IX_CloudDirectories_OwnerId_Name`: `(OwnerId, Name) WHERE ParentId IS NULL` — имена корневых папок. Отдельный индекс нужен, поскольку PostgreSQL считает NULL различными в прежнем составном ключе.
- `IX_CloudDirectories_OwnerId_SystemKind`: `(OwnerId, SystemKind) WHERE SystemKind <> 0` — один системный тип на владельца независимо от имени и расположения; обычные папки с `None` исключены.

`CloudHierarchyStorage.AddDirectory` и `UpdateDirectory` преобразуют только PostgreSQL `23505` двух индексов имён в прежний `DirectoryNameConflictException`. Неуспешная сущность отсоединяется, чтобы следующая запись не повторяла конфликт. Другие ошибки не маскируются. `UpdateDirectory` сохраняет изменения отслеживаемой папки (`GetDirectory`) без `Update(entity)`, поэтому устаревший снимок при переименовании/переносе не перезаписывает `SystemKind`.

`EnsureSystemDirectory` идемпотентен при параллельных вызовах:
- Сначала перечитывает папку по типу через `AsNoTracking`; переименованная/перемещённая системная папка сохраняет свой ID.
- Обычная корневая папка с каноническим именем повышается через `PromoteSystemDirectory`: условный `ExecuteUpdate` проверяет ID, владельца, имя, `ParentId == null` и `SystemKind == None`. При изменении строки повторяется чтение. Во внешней транзакции для этой записи создаётся savepoint, при ошибке он откатывается и освобождается.
- Другой системный тип с каноническим именем сохраняется; новая папка получает свободное имя через `UniqueNameResolver` (` (1)`, ` (2)`…). Если при пустом первом чтении обычная папка появилась конкурентно, пробуется исходное имя: конфликт корневого индекса приводит к повторному чтению и повышению, а не к лишнему суффиксу.
- Конфликт вставки корневого/системного индекса откатывается `SaveChanges`; отсоединяется только проигравшая Added-сущность. Следующая итерация возвращает ID победителя или заново выбирает имя. Повторы учитывают отмену запроса; чужие unique-нарушения не повторяются.

Миграция `20261002020415_EnforceUniqueCloudDirectories` в одной транзакции берёт `SHARE ROW EXCLUSIVE` на `CloudDirectories`, проверяет существующие дубли и только затем меняет индексы. При дублях выдаёт ошибку `F16` с владельцем, ключом и ID (до 20 групп каждого типа), полностью откатывается и **не меняет данные**. Оператор разрешает дубли отдельно, сохраняя нужные папки/ссылки; затем миграция повторяется. `Down` убирает корневой индекс и возвращает системному прежнюю неуникальность. При выкладке остановить записи всех реплик Files на время миграции; сервис применяет её при старте.

Тесты: `CloudDirectoryUniquenessPostgresTests` (18 сценариев: создания, переименование/перенос ↔ создание, повышение ↔ создание во внешних транзакциях, запись тем же контекстом после конфликта, суффиксы, условное повышение, устаревший `SystemKind`, границы владельца/родителя и отмена); `CloudDirectoryMigrationPostgresTests` (4 сценария: оба вида старых дублей, ограничение диагностики, up/down/up с файлами/публичными ссылками/грантами). Хелпер `PostgresFilesDatabase.CreateAsync(targetMigration)` поддерживает старую схему, `CreateContext(interceptors)` — барьеры перед реальной записью; базы изолированы (`barkcloud_files_test_<guid>`). Проверено на PostgreSQL 18.6: 449 тестов Files, без ошибок/пропусков; EF `has-pending-model-changes` не обнаруживает расхождений. API/proto и регистр/нормализация имён не менялись.

## Host (gRPC)

`Backend/BarkCloud.Files/Host/CloudApiService.cs`:
- Наследует `CloudApi.CloudApiBase` (из `BarkCloud.Proto.Files`)
- `[Authorize(Policy = nameof(TokenType.User))]` — требует токен типа User ([[modules/shared-identity]])
- Тонкий слой: каждый метод оборачивает аргументы в Command и шлёт через MediatR

## Features

`Backend/BarkCloud.Files/Features/Cloud/` — каждая пара `XxxCommand.cs` + `XxxCommandHandler.cs`:

### Директории
- `CreateDirectory` — создать папку (возвращает `DirectoryInfo`; под `LockTree`)
- `RenameDirectory` — переименовать
- `MoveDirectory` — переместить в другую папку (под `LockTree`, см. «Целостность дерева»)
- `DeleteDirectory` — удалить рекурсивно (под `LockTree`)
- `ListDirectory` — cursor-страница (subdirs + files), только метаданные
- `ListDirectoryDetailed` — cursor-страница с обогащёнными `FileEntryDetailed` (полная `UploadFileInfo` с URL/превью); записи без ready-блоба не возвращаются

Для обоих RPC размер страницы файлов по умолчанию — 50, максимум — 200. Курсор состоит из `(Name, entry_id)`, порядок — `Name ASC, entry_id ASC`; выборка делает `limit + 1`, чтобы определить `next_cursor_*`. Поддиректории не пагинируются. Удалённые и неготовые файлы не попадают в ответ.

### Записи о файлах
- `AttachFile` — привязать существующий **ready** `UploadFile` к папке (создаёт `CloudFileEntry`; под `LockTree`, включая выбор системной папки); processing placeholder отклоняется `FileNotReadyException`; повтор уже привязанного файла даёт стабильный `FileAlreadyAttachedException`; коллизия имени разрешается суффиксом ` (1)`; при `route_by_media_kind=true` `directory_id` игнорируется и файл кладётся в системную папку по типу медиа
- `RenameFileEntry` — изменить отображаемое имя живой записи (под `LockTree`; корзина → `FileEntryNotFound`)
- `MoveFileEntry` — перенести живую запись в другую папку (под `LockTree`; корзина → `FileEntryNotFound`)
- `DeleteFileEntry` — **перемещает запись в корзину** под `LockTree` (soft-delete: `IsDeleted/DeletedAt/PurgeAt`). `Uploaders`/квота сохраняются, блоб не трогается
- `DeleteFileEntries` — массовый вариант soft-delete для записей каталога: принимает набор `entry_id`, дедуплицирует, чужие/несуществующие/уже удалённые записи молча пропускает и возвращает `deleted_count`. Web использует его через `/api/cloud/entries/delete`.
- `DeleteDirectory` — рекурсивно: файлы поддерева → в корзину, сами папки удаляются сразу (restore вернёт файлы в корень). Дополнительно немедленно снимает публичность (`FolderShareLink`) и приватные гранты (`DirectoryGrant`) со всех папок поддерева — публичная страница `/f` и доступ получателей прекращаются сразу

> `CopyFileEntry` **удалён** в рамках инварианта «одна директория на файл».

> **Системные папки и авто-распределение**: `CloudDirectory.SystemKind` (None/Photos/Videos/Music/OtherDocuments) помечает системные папки «Фото»/«Видео»/«Музыка»/«Другие документы» — находятся по флагу (устойчивы к переименованию), создаются лениво (`EnsureSystemDirectory`). При `route_by_media_kind` сервер кладёт фото→«Фото», видео→«Видео», аудио→«Музыка», прочее→«Другие документы». Клиентская папка «Недавно загруженные» больше не используется. При явной папке (перетаскивание в открытую папку, Windows-диск) распределение не применяется; уже загруженные аудиофайлы не перемещаются.

### Корзина
- `ListTrash` — список записей в корзине (от свежеудалённых к старым); cursor-пагинация `(DeletedAt + entry_id)`; `TrashEntry` = `FileEntryInfo` + `UploadFileInfo` + `deleted_at`/`purge_at`
- `RestoreFromTrash` — восстановить запись (под `LockTree`; в исходную папку или, если она удалена, в корень; конфликт имени разрешается суффиксом; отказ при нарушении инварианта одной директории)
- `DeleteFromTrash` — удалить запись из корзины навсегда (немедленно) → `TrashPurgeService`
- `EmptyTrash` — очистить корзину владельца целиком → `TrashPurgeService`

### Галерея
- `ListUserImages` — **[deprecated]** все изображения пользователя; cursor-пагинация. Исключает превью-блобы. Заменён на `ListUserMedia`
- `ListUserMedia` — медиа пользователя по `MediaKind` (PHOTO/VIDEO) от новых к старым; cursor-пагинация; исключает превью-блобы (`!FilePreviews.Any(p => p.PreviewFileId == f.Id)`) и «эффективно удалённые» файлы (все записи владельца в корзине)
- `SetVideoThumbnail` — заменить превью видео загруженной картинкой (проверка владения, пересоздание `FilePreview` через `PreviewPersistenceService`)

### Навигация
- `GetPath` — построить путь до объекта (директории/записи) в иерархии

### Поиск
- `SearchFiles` (`Features/Cloud/SearchFiles/`) — поиск живых записей файлов владельца по подстроке имени (по всему облаку, независимо от папок). Хранилище: `ICloudHierarchyStorage.SearchFileEntriesPage` (`Name.ToLower().Contains`, `!IsDeleted`, сортировка `(CreatedAt desc, Id desc)`, cursor-пагинация). Обогащение `FileEntryDetailed` как в `ListDirectoryDetailed`. Host — `CloudApiService.SearchFiles`.

### Публичные альбомы (`/al/{token}`)
- Зеркало публичных папок ([[modules/backend-files]] · `FolderShareLink`): сущность `Domain/AlbumShareLink` (Owner/AlbumId/Token/Name/CreatedAt/ClickCount), хранилище `IAlbumShareStorage`/`AlbumShareStorage` (миграция `AddAlbumShareLinks`), фичи `Features/Cloud/{CreateAlbumShare,ListMyAlbumShares,RevokeAlbumShare,ResolveAlbumShare}`.
- `CreateAlbumShare` идемпотентен (один шар на альбом, индекс `(OwnerId, AlbumId)` unique). `ResolveAlbumShare` — анонимный (`FilesServerApiService`, политика Service): листинг элементов альбома с temp-URL/превью (как `ResolveFolderShare`), cursor-пагинация, исключает «эффективно удалённые» файлы. `DeleteAlbum` снимает публичность (`RemoveByAlbum`).

## gRPC API

См. отдельный раздел в [[api/files-api]] · `CloudApi`.

## Связь с UploadFile

`CloudFileEntry.FileId → UploadFile.Id`. Удаление `CloudFileEntry` не каскадирует на `UploadFile`. `UploadFileType.CloudFile = 2` (см. [[modules/backend-files]] · Domain) — тип, который ассоциируется с пользовательским облачным хранилищем. После [[modules/upload-2]] общая готовность означает `UploadedAt != null && Etag != null/empty` и задаётся `UploadFileReadiness.WhereReady()`: list/search/trash/share/music/download/metadata/activity пути скрывают всё остальное, а `AttachFile`/`DeleteUserMedia` отклоняют pre-ready ID.
