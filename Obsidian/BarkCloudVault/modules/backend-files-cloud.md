# Backend — Files · Cloud (иерархия папок)

Parent: [[modules/backend-files]] · See also: [[api/files-api]]

## Назначение

NextCloud-подобная иерархия папок и файловых записей пользователя поверх существующего хранилища `UploadFile`. Папки (`CloudDirectory`) образуют дерево; записи (`CloudFileEntry`) ссылаются на реальные `UploadFile` и могут иметь своё отображаемое имя независимо от исходного `Filename`.

Корневая папка владельца **не материализуется** — представлена `ParentId == null` для директорий и синтетическим `Guid.Empty` для `CloudFileEntry.DirectoryId` (нужно для уникального индекса `(OwnerId, DirectoryId, Name)`).

**Инвариант «одна директория на файл»**: блоб владельца может быть привязан максимум к одной директории. Гарантируется уникальным индексом `CloudFileEntries(OwnerId, FileId)` и проверкой в `AttachFile` (`FileAlreadyAttachedException`). Альбомы — отдельный слой many-to-many ([[modules/backend-files]]).

**Корзина (soft-delete)**: удаление файла/папки не удаляет данные сразу, а помечает записи `CloudFileEntry` как `IsDeleted` с датами `DeletedAt`/`PurgeAt` (хранение 14 дней). Уникальные индексы — **частичные** (`WHERE IsDeleted = false`), поэтому запись в корзине не блокирует повторную загрузку файла/имени. Записи в корзине исключаются из иерархии, галереи и альбомов, но `Uploaders` сохраняются (квота не освобождается). Окончательная зачистка (БД + S3 + превью + альбомы + избранное + публичные ссылки) — `TrashPurgeService` + фоновый `TrashCleanupService` (раз в 6 ч); осиротевшие блобы (пустой `Uploaders`) добивает фоновый `OrphanBlobCleanupService`. См. [[modules/backend-files]].

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

Миграции: `Persistence/Migrations/20260518174041_AddCloudDirectories.cs`; `20260525213058_AddTrashToCloudFileEntries.cs` (поля корзины + частичные уникальные индексы `WHERE IsDeleted = false` + индекс по `PurgeAt WHERE IsDeleted = true`).

## Host (gRPC)

`Backend/BarkCloud.Files/Host/CloudApiService.cs`:
- Наследует `CloudApi.CloudApiBase` (из `BarkCloud.Proto.Files`)
- `[Authorize(Policy = nameof(TokenType.User))]` — требует токен типа User ([[modules/shared-identity]])
- Тонкий слой: каждый метод оборачивает аргументы в Command и шлёт через MediatR

## Features

`Backend/BarkCloud.Files/Features/Cloud/` — каждая пара `XxxCommand.cs` + `XxxCommandHandler.cs`:

### Директории
- `CreateDirectory` — создать папку (возвращает `DirectoryInfo`)
- `RenameDirectory` — переименовать
- `MoveDirectory` — переместить в другую папку
- `DeleteDirectory` — удалить рекурсивно
- `ListDirectory` — листинг (subdirs + files), только метаданные
- `ListDirectoryDetailed` — листинг с обогащёнными `FileEntryDetailed` (полная `UploadFileInfo` с URL/превью)

### Записи о файлах
- `AttachFile` — привязать существующий `UploadFile` к папке (создаёт `CloudFileEntry`); отказывает, если файл уже привязан к директории владельца (`FileAlreadyAttachedException`); коллизия имени в папке разрешается суффиксом ` (1)`; при `route_by_media_kind=true` `directory_id` игнорируется и файл кладётся в системную папку по типу медиа
- `RenameFileEntry` — изменить отображаемое имя записи
- `MoveFileEntry` — перенести в другую папку
- `DeleteFileEntry` — **перемещает запись в корзину** (soft-delete: `IsDeleted/DeletedAt/PurgeAt`). `Uploaders`/квота сохраняются, блоб не трогается
- `DeleteDirectory` — рекурсивно: файлы поддерева → в корзину, сами папки удаляются сразу (restore вернёт файлы в корень). Дополнительно немедленно снимает публичность (`FolderShareLink`) и приватные гранты (`DirectoryGrant`) со всех папок поддерева — публичная страница `/f` и доступ получателей прекращаются сразу

> `CopyFileEntry` **удалён** в рамках инварианта «одна директория на файл».

> **Системные папки и авто-распределение**: `CloudDirectory.SystemKind` (None/Photos/Videos/OtherDocuments) помечает системные папки «Фото»/«Видео»/«Другие документы» — находятся по флагу (устойчивы к переименованию), создаются лениво (`EnsureSystemDirectory`). При `route_by_media_kind` сервер кладёт фото→«Фото», видео→«Видео», прочее→«Другие документы». Клиентская папка «Недавно загруженные» больше не используется. При явной папке (перетаскивание в открытую папку, Windows-диск) распределение не применяется.

### Корзина
- `ListTrash` — список записей в корзине (от свежеудалённых к старым); cursor-пагинация `(DeletedAt + entry_id)`; `TrashEntry` = `FileEntryInfo` + `UploadFileInfo` + `deleted_at`/`purge_at`
- `RestoreFromTrash` — восстановить запись (в исходную папку или, если она удалена, в корень; конфликт имени разрешается суффиксом; отказ при нарушении инварианта одной директории)
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

`CloudFileEntry.FileId → UploadFile.Id`. Удаление `CloudFileEntry` не каскадирует на `UploadFile`. `UploadFileType.CloudFile = 2` (см. [[modules/backend-files]] · Domain) — тип, который ассоциируется с пользовательским облачным хранилищем.
