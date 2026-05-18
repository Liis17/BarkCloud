# Backend — Files

Parent: [[index]] · See also: [[api/files-api]] · [[modules/backend-files-cloud]] · [[modules/shared-queue]]

## Назначение

Сервис файлов. Две основные ответственности:
1. **Загрузка/скачивание**: presigned URL в MinIO, дедупликация по хешу, квоты, временные файлы, сжатие изображений
2. **Облачная иерархия** (NextCloud-подобная): папки + записи о файлах — см. дочернюю заметку [[modules/backend-files-cloud]]

## Расположение

`Backend/BarkCloud.Files/`

## Файлы

### Domain
- `UploadFile.cs` — загруженный файл (реальный объект в S3)
- `UploadFileType.cs` — enum типов: `Unknown=0`, `UserAvatar=1`, `CloudFile=2`
- `FileHash.cs` — хеш для дедупликации
- `TempFile.cs` — временный файл (TTL)
- `CloudDirectory.cs` — папка в облачной иерархии → [[modules/backend-files-cloud]]
- `CloudFileEntry.cs` — запись о файле в иерархии → [[modules/backend-files-cloud]]

### Host
- `FilesApiService.cs` — клиентский gRPC `FilesApi`
- `FilesServerApiService.cs` — серверный gRPC `FilesServerApi`
- `CloudApiService.cs` — gRPC `CloudApi` → [[modules/backend-files-cloud]]
- `FilesController.cs` — HTTP-контроллер (прямые upload/download)

### Services
- `ImageCompressor.cs` — сжатие изображений
- `TempFileCleanupService.cs` — фоновая очистка временных файлов (BackgroundService)

### Infrastructure
- `S3BucketInitializer.cs` — создание/проверка бакетов MinIO при старте
- `S3BucketRegistry.cs` — реестр бакетов
- `S3Uploader.cs` — обёртка над загрузкой в S3/MinIO

### Configurations
- `BucketS3Options.cs` — настройки S3-бакета

### Persistence
- `FilesContext.cs`, `FilesContextFactory.cs` — EF Core DbContext (содержит `UploadFiles`, `FileHashes`, `TempFiles`, `CloudDirectories`, `CloudFileEntries`)
- `UploadedFilesStorage.cs`
- `FileHashesStorage.cs`
- `TempFilesStorage.cs`
- `CloudHierarchyStorage.cs` — см. [[modules/backend-files-cloud]]
- `Migrations/`:
  - `20260518172338_InitialCreate.cs`
  - `20260518174041_AddCloudDirectories.cs` — добавляет таблицы Cloud

### Exceptions (локальные)
- `FileAlreadyUploadedException.cs`
- `FileNotUploadedException.cs`

### Consumers
- `SessionRevokedConsumer.cs` — слушает `SessionRevokedEvent` из [[modules/shared-queue]]

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

### Облачная иерархия (вложенно в `Features/Cloud/`)

10 фич: `CreateDirectory`, `RenameDirectory`, `MoveDirectory`, `DeleteDirectory`, `ListDirectory`, `AttachFile`, `RenameFileEntry`, `MoveFileEntry`, `DeleteFileEntry`, `GetPath`. Подробнее — [[modules/backend-files-cloud]].

> **Не реализовано**: стикеры, бейджи, постеры — нет ни в `Features/`, ни в `Shared/BarkCloud.Proto/files_api.proto`.

## Зависимости

- Использует: `BarkCloud.Proto`, `BarkCloud.GrpcServer`, `BarkCloud.Shared.*`, EF Core, PostgreSQL, MinIO (S3 SDK), RabbitMQ, MediatR
- Тесно связан с MinIO (см. [[structure/infrastructure]])
