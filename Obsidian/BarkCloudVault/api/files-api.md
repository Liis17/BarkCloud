# gRPC API — Files

Parent: [[index]] · Module: [[modules/backend-files]] · Cloud: [[modules/backend-files-cloud]] · Proto: [[modules/shared-proto]]

Файл: `Shared/BarkCloud.Proto/files_api.proto`
Namespace C#: `BarkCloud.Proto.Files`
Package: `barkcloud.files`

В proto-файле определены **три сервиса**: `FilesApi` (клиент), `CloudApi` (клиент, облачная иерархия), `FilesServerApi` (служебный).

## Сервис: `FilesApi` (клиентский)

Все RPC реализованы:

| RPC | Назначение |
|-----|-----------|
| `GetUploadUrl(GetUploadUrlRequest) → GetUploadUrlResponse` | Получить presigned URL для загрузки |
| `GetTempDownloadUrl(GetTempDownloadUrlRequest) → GetTempDownloadUrlResponse` | Ссылки на скачивание + превью (`file_id`, `url`, `preview_url`) |
| `CheckFileHash(CheckFileHashRequest) → CheckFileHashResponse` | Проверить дедупликацию по хешу |
| `GetUserStorageInfo(GetUserStorageInfoRequest) → GetUserStorageInfoResponse` | Инфо о квоте/использовании |

## Сервис: `CloudApi` (клиентский, облачная иерархия)

Все RPC реализованы. Хост — `CloudApiService` с авторизацией `[Authorize(Policy = nameof(TokenType.User))]`. Подробнее — [[modules/backend-files-cloud]].

| RPC | Назначение |
|-----|-----------|
| `CreateDirectory(CreateDirectoryRequest) → DirectoryInfo` | Создать папку |
| `RenameDirectory(RenameDirectoryRequest) → CloudEmpty` | Переименовать папку |
| `MoveDirectory(MoveDirectoryRequest) → CloudEmpty` | Переместить папку |
| `DeleteDirectory(DeleteDirectoryRequest) → CloudEmpty` | Удалить рекурсивно |
| `ListDirectory(ListDirectoryRequest) → DirectoryListing` | Листинг (`subdirs`, `files`); `directory_id` пуст = корень владельца |
| `AttachFile(AttachFileRequest) → CloudEmpty` | Привязать загруженный `UploadFile` к папке (создаёт `CloudFileEntry`) |
| `RenameFileEntry(RenameFileEntryRequest) → CloudEmpty` | Переименовать запись (не меняет `UploadFile.Filename`) |
| `MoveFileEntry(MoveFileEntryRequest) → CloudEmpty` | Переместить запись (`new_directory_id` пуст = корень) |
| `DeleteFileEntry(DeleteFileEntryRequest) → CloudEmpty` | Удалить запись (`UploadFile` не трогается) |
| `GetPath(GetPathRequest) → PathResponse` | Построить путь до объекта в иерархии |

### Messages CloudApi

- `CloudEmpty {}` — пустой ответ
- `DirectoryInfo` — информация о папке
- `FileEntryInfo` — содержит `directory_id` (папка-владелец записи)
- `DirectoryListing { repeated DirectoryInfo subdirs; repeated FileEntryInfo files; }`
- Запросы: `Create/Rename/Move/Delete/ListDirectoryRequest`, `Attach/Rename/Move/DeleteFileEntryRequest`, `GetPathRequest`
- `ListDirectoryRequest.directory_id` — `optional string`, пустая/неуказанная = корень
- `PathResponse` — путь до объекта

## Сервис: `FilesServerApi` (служебный)

Все RPC реализованы:

| RPC | Назначение |
|-----|-----------|
| `GetFileData(GetFileDataRequest) → GetFileDataResponse` | Информация о загруженном файле |
| `GetFilesData(GetFilesDataRequest) → GetFilesDataResponse` | Информация о нескольких файлах |
| `GetUserStorageInfoServer(GetUserStorageInfoServerRequest) → GetUserStorageInfoResponse` | Storage info (админка) |
| `UploadAvatarServer(UploadAvatarServerRequest) → UploadAvatarServerResponse` | Загрузка аватарки пользователя (служебно) |

## Что отсутствует в proto и коде

- Стикерпаки и стикеры
- Загрузка изображений бейджей и постеров
- Прямая HTTP-стримовая загрузка/скачивание — есть только `FilesController` (без proto-описания)

`UploadFileType`: `Unknown=0`, `UserAvatar=1`, `CloudFile=2` (`CLOUD_FILE = 2` в proto enum).

## Типизированные ошибки

- Локальные: `Exceptions/FileAlreadyUploadedException`, `Exceptions/FileNotUploadedException`
- Общие из [[modules/shared-exceptions]] · Files: `FileNotFoundException`, `NotValidFileIdException`

## Связь с инфраструктурой

Все загрузки/скачивания через **MinIO** (S3-совместимое):
- `Infrastructure/S3Uploader.cs`
- `Infrastructure/S3BucketInitializer.cs`
- `Infrastructure/S3BucketRegistry.cs`
- `Configurations/BucketS3Options.cs`

Сжатие — `Services/ImageCompressor.cs`. Очистка временных — `Services/TempFileCleanupService.cs` (background).
