# Backend — Files · Cloud (иерархия папок)

Parent: [[modules/backend-files]] · See also: [[api/files-api]]

## Назначение

NextCloud-подобная иерархия папок и файловых записей пользователя поверх существующего хранилища `UploadFile`. Папки (`CloudDirectory`) образуют дерево; записи (`CloudFileEntry`) ссылаются на реальные `UploadFile` и могут иметь своё отображаемое имя независимо от исходного `Filename`.

Корневая папка владельца **не материализуется** — представлена `ParentId == null` для директорий и синтетическим `Guid.Empty` для `CloudFileEntry.DirectoryId` (нужно для уникального индекса `(OwnerId, DirectoryId, Name)`).

**Инвариант «одна директория на файл»**: блоб владельца может быть привязан максимум к одной директории. Гарантируется уникальным индексом `CloudFileEntries(OwnerId, FileId)` и проверкой в `AttachFile` (`FileAlreadyAttachedException`). Альбомы — отдельный слой many-to-many ([[modules/backend-files]]).

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

## Persistence

`Backend/BarkCloud.Files/Persistence/CloudHierarchyStorage.cs`:
- Константа `RootDirectoryId = Guid.Empty` — синтетический корень для уникального индекса
- Методы доступа к `CloudDirectories` и `CloudFileEntries` (`Get*`, `*AsNoTracking`, и др.)
- Подключён к `FilesContext` (`CloudDirectories`, `CloudFileEntries` DbSet'ы)

Миграция: `Persistence/Migrations/20260518174041_AddCloudDirectories.cs`.

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
- `AttachFile` — привязать существующий `UploadFile` к папке (создаёт `CloudFileEntry`); отказывает, если файл уже привязан к директории владельца (`FileAlreadyAttachedException`)
- `RenameFileEntry` — изменить отображаемое имя записи
- `MoveFileEntry` — перенести в другую папку
- `DeleteFileEntry` — удалить запись (не удаляет сам `UploadFile`, декремент `Uploaders` только если у владельца не осталось других копий)

> `CopyFileEntry` **удалён** в рамках инварианта «одна директория на файл».

### Галерея
- `ListUserImages` — **[deprecated]** все изображения пользователя; cursor-пагинация. Исключает превью-блобы. Заменён на `ListUserMedia`
- `ListUserMedia` — медиа пользователя по `MediaKind` (PHOTO/VIDEO) от новых к старым; cursor-пагинация; исключает превью-блобы (`!FilePreviews.Any(p => p.PreviewFileId == f.Id)`)
- `SetVideoThumbnail` — заменить превью видео загруженной картинкой (проверка владения, пересоздание `FilePreview` через `PreviewPersistenceService`)

### Навигация
- `GetPath` — построить путь до объекта (директории/записи) в иерархии

## gRPC API

См. отдельный раздел в [[api/files-api]] · `CloudApi`.

## Связь с UploadFile

`CloudFileEntry.FileId → UploadFile.Id`. Удаление `CloudFileEntry` не каскадирует на `UploadFile`. `UploadFileType.CloudFile = 2` (см. [[modules/backend-files]] · Domain) — тип, который ассоциируется с пользовательским облачным хранилищем.
