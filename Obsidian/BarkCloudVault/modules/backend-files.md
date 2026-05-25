# Backend — Files

Parent: [[index]] · See also: [[api/files-api]] · [[modules/backend-files-cloud]] · [[modules/shared-queue]]

## Назначение

Сервис файлов. Основные ответственности:
1. **Загрузка/скачивание**: presigned URL в MinIO, дедупликация по хешу, квоты, временные файлы, сжатие изображений, превью видео (FFmpeg)
2. **Облачная иерархия** (NextCloud-подобная): папки + записи о файлах — см. дочернюю заметку [[modules/backend-files-cloud]]
3. **Галерея и альбомы**: классификация медиа (`MediaKind`), раздельные списки фото/видео (`ListUserMedia`), универсальные альбомы фото/видео (`AlbumApi`)

## Расположение

`Backend/BarkCloud.Files/`

## Файлы

### Domain
- `UploadFile.cs` — загруженный файл (реальный объект в S3); содержит `MediaKind`
- `UploadFileType.cs` — enum типов: `Unknown=0`, `UserAvatar=1`, `CloudFile=2`
- `MediaKind.cs` — категория медиа: `Other=0`, `Photo=1`, `Video=2`, `Document=3`, `Audio=4` (заполняется при загрузке по content-type)
- `FileHash.cs` — хеш для дедупликации
- `FilePreview.cs` — связка оригинал→превью (превью хранится как отдельный `UploadFile`)
- `TempFile.cs` — временный файл (TTL)
- `CloudDirectory.cs` — папка в облачной иерархии → [[modules/backend-files-cloud]]
- `CloudFileEntry.cs` — запись о файле в иерархии → [[modules/backend-files-cloud]]
- `Album.cs` — альбом (универсальная коллекция фото/видео): `Name`, `Description`, `CoverFileId`
- `AlbumItem.cs` — привязка файла к альбому (many-to-many)

### Host
- `FilesApiService.cs` — клиентский gRPC `FilesApi`
- `FilesServerApiService.cs` — серверный gRPC `FilesServerApi`
- `CloudApiService.cs` — gRPC `CloudApi` (иерархия + галерея `ListUserMedia` + `SetVideoThumbnail`) → [[modules/backend-files-cloud]]
- `AlbumApiService.cs` — gRPC `AlbumApi` (альбомы)
- `FilesController.cs` — HTTP-контроллер (прямые upload/download)

### Services
- `ImageCompressor.cs` — сжатие изображений
- `VideoThumbnailExtractor.cs` — извлечение кадра-обложки и размеров видео через FFMpegCore (кадр на 5-й секунде)
- `PreviewPersistenceService.cs` — сохранение превью (дедуп по SHA256 + S3 + `FilePreview`); общий для загрузки и `SetVideoThumbnail`
- `AlbumViewBuilder.cs` — сборка `AlbumInfo` (счётчик элементов + URL превью обложки) батчем
- `TempFileCleanupService.cs` — фоновая очистка временных файлов (BackgroundService)
- `TrashPurgeService.cs` — окончательная зачистка корзины: снятие `Uploaders`, удаление из альбомов (`AlbumItems`), удаление записей/превью и **физическое удаление осиротевших блобов из S3**. Общий для ручных RPC и воркера. Константа `Retention = 14 дней`
- `TrashCleanupService.cs` — фоновый воркер (BackgroundService, раз в 6 ч): зачищает записи корзины с истёкшим `PurgeAt` через `TrashPurgeService`

### Infrastructure
- `S3BucketInitializer.cs` — создание/проверка бакетов MinIO при старте
- `S3BucketRegistry.cs` — реестр бакетов
- `S3Uploader.cs` — обёртка над S3/MinIO: `UploadAsync`, `DownloadAsync`, `DeleteAsync` (удаление объекта, идемпотентно — используется зачисткой корзины)

### Configurations
- `BucketS3Options.cs` — настройки S3-бакета

### Persistence
- `FilesContext.cs`, `FilesContextFactory.cs` — EF Core DbContext (содержит `UploadedFiles`, `FileHashes`, `TempFiles`, `CloudDirectories`, `CloudFileEntries`, `FilePreviews`, `Albums`, `AlbumItems`)
- `UploadedFilesStorage.cs`
- `FileHashesStorage.cs`
- `TempFilesStorage.cs`
- `CloudHierarchyStorage.cs` — см. [[modules/backend-files-cloud]]; метод `FileEntryExistsForFile` для инварианта одной директории
- `AlbumStorage.cs` — CRUD альбомов и их элементов, cursor-пагинация
- `Migrations/`:
  - `20260518172338_InitialCreate.cs`
  - `20260518174041_AddCloudDirectories.cs` — добавляет таблицы Cloud
  - `20260518180038_AddFilePreviews.cs` — таблица `FilePreviews`
  - `20260524204149_AddMediaKindAndAlbums.cs` — колонка `MediaKind` (+ бэкафилл), таблицы `Albums`/`AlbumItems`, уникальный индекс `CloudFileEntries(OwnerId, FileId)` с дедупликацией
  - `20260525213058_AddTrashToCloudFileEntries.cs` — корзина: колонки `IsDeleted`/`DeletedAt`/`PurgeAt` в `CloudFileEntries`, частичные уникальные индексы (`WHERE IsDeleted = false`), индекс по `PurgeAt` (`WHERE IsDeleted = true`)

### Exceptions (локальные)
- `FileAlreadyUploadedException.cs`
- `FileNotUploadedException.cs`

### Consumers
- `SessionRevokedConsumer.cs` — слушает `SessionRevokedEvent` из [[modules/shared-queue]]
- `UserDeletedConsumer.cs` — по `UserDeleted` (из [[modules/backend-users]]) снимает пользователя из `Uploaders` всех его блобов (освобождает квоту) и удаляет его `CloudDirectories`/`CloudFileEntries`/`Albums`/`AlbumItems`. Физическое удаление осиротевших S3-блобов не делает (как и ручное удаление)

### Прочее
- `Extensions/FileExtensions.cs`, `Extensions/ServiceCollectionExtensions.cs`
- `Helpers/FileUrlHelper.cs`
- `Mapping/UploadFileMapping.cs`
- `Dockerfile`, `Dockerfile.slim`
- `BarkCloud.Files.http` — HTTP-запросы для ручной отладки

## Features (vertical slices)

### Загрузка/скачивание/метаданные (плоско в `Features/`)

| Feature | Назначение |
|---------|-----------|
| `GetUploadUrl` | Выдать presigned URL для загрузки |
| `UploadFile` | Серверная загрузка файла |
| `GetTempDownloadUrl` | Временные ссылки на скачивание + превью |
| `DownloadFile` | Скачивание (через контроллер) |
| `CheckFileHash` | Проверка дедупликации |
| `GetFileData` / `GetFilesData` | Метаданные файла(ов) |
| `GetUserStorageInfo` / `GetUserStorageInfoServer` | Информация о квоте |
| `UploadAvatarServer` | Загрузка аватара пользователя (служебный) |

### Облачная иерархия + галерея (вложенно в `Features/Cloud/`)

`CreateDirectory`, `RenameDirectory`, `MoveDirectory`, `DeleteDirectory`, `ListDirectory`, `ListDirectoryDetailed`, `AttachFile`, `RenameFileEntry`, `MoveFileEntry`, `DeleteFileEntry`, `GetPath`, `ListUserImages` (deprecated), `ListUserMedia` (фото/видео по `MediaKind`), `SetVideoThumbnail`. **Корзина**: `ListTrash`, `RestoreFromTrash`, `DeleteFromTrash`, `EmptyTrash` (`DeleteFileEntry`/`DeleteDirectory` теперь soft-delete). `CopyFileEntry` **удалён** (инвариант одной директории). Подробнее — [[modules/backend-files-cloud]].

### Альбомы (вложенно в `Features/Album/`)

7 фич: `CreateAlbum`, `UpdateAlbum`, `DeleteAlbum`, `AddItemsToAlbum`, `RemoveItemsFromAlbum`, `ListAlbums`, `ListAlbumItems`. См. [[api/files-api]] · `AlbumApi`.

> **Не реализовано**: стикеры, бейджи, постеры — нет ни в `Features/`, ни в `Shared/BarkCloud.Proto/files_api.proto`.

## Зависимости

- Использует: `BarkCloud.Proto`, `BarkCloud.GrpcServer`, `BarkCloud.Shared.*`, EF Core, PostgreSQL, MinIO (S3 SDK), RabbitMQ, MediatR, **SixLabors.ImageSharp** (превью изображений), **FFMpegCore** (превью видео)
- Образ Files содержит бинарь `ffmpeg`/`ffprobe` (COPY из `mwader/static-ffmpeg` в `Dockerfile`/`Dockerfile.slim`)
- Тесно связан с MinIO (см. [[structure/infrastructure]])
