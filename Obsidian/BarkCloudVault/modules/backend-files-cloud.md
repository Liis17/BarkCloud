# Backend — Files · Cloud (иерархия папок)

Parent: [[modules/backend-files]] · See also: [[api/files-api]]

## Назначение

NextCloud-подобная иерархия папок и файловых записей пользователя поверх существующего хранилища `UploadFile`. Папки (`CloudDirectory`) образуют дерево; записи (`CloudFileEntry`) ссылаются на реальные `UploadFile` и могут иметь своё отображаемое имя независимо от исходного `Filename`.

Корневая папка владельца **не материализуется** — представлена `ParentId == null` для директорий и синтетическим `Guid.Empty` для `CloudFileEntry.DirectoryId` (нужно для уникального индекса `(OwnerId, DirectoryId, Name)`).

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

`Backend/BarkCloud.Files/Features/Cloud/` — 10 фич, каждая пара `XxxCommand.cs` + `XxxCommandHandler.cs`:

### Директории
- `CreateDirectory` — создать папку (возвращает `DirectoryInfo`)
- `RenameDirectory` — переименовать
- `MoveDirectory` — переместить в другую папку
- `DeleteDirectory` — удалить рекурсивно
- `ListDirectory` — листинг (subdirs + files)

### Записи о файлах
- `AttachFile` — привязать существующий `UploadFile` к папке (создаёт `CloudFileEntry`)
- `RenameFileEntry` — изменить отображаемое имя записи
- `MoveFileEntry` — перенести в другую папку
- `DeleteFileEntry` — удалить запись (не удаляет сам `UploadFile`)

### Навигация
- `GetPath` — построить путь до объекта (директории/записи) в иерархии

## gRPC API

См. отдельный раздел в [[api/files-api]] · `CloudApi`.

## Связь с UploadFile

`CloudFileEntry.FileId → UploadFile.Id`. Удаление `CloudFileEntry` не каскадирует на `UploadFile`. `UploadFileType.CloudFile = 2` (см. [[modules/backend-files]] · Domain) — тип, который ассоциируется с пользовательским облачным хранилищем.
