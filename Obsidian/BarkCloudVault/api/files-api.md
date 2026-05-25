# gRPC API — Files

Parent: [[index]] · Module: [[modules/backend-files]] · Cloud: [[modules/backend-files-cloud]] · Proto: [[modules/shared-proto]] · Клиентский гайд: [[api/files-client-guide]]

Файл: `Shared/BarkCloud.Proto/files_api.proto`
Namespace C#: `BarkCloud.Proto.Files`
Package: `barkcloud.files`

В proto-файле определены **четыре сервиса**: `FilesApi` (клиент), `CloudApi` (клиент, облачная иерархия + галерея), `FilesServerApi` (служебный), `AlbumApi` (клиент, альбомы фото/видео).

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
| `ListDirectory(ListDirectoryRequest) → DirectoryListing` | Листинг (`subdirs`, `files`); `directory_id` пуст = корень владельца. Только метаданные |
| `ListDirectoryDetailed(ListDirectoryRequest) → DirectoryListingDetailed` | Та же выборка, что у `ListDirectory`, но каждый `FileEntryDetailed` содержит полный `UploadFileInfo` (URL, превью 128/512/1024, размеры) |
| `AttachFile(AttachFileRequest) → CloudEmpty` | Привязать загруженный `UploadFile` к папке (создаёт `CloudFileEntry`) |
| `RenameFileEntry(RenameFileEntryRequest) → CloudEmpty` | Переименовать запись (не меняет `UploadFile.Filename`) |
| `MoveFileEntry(MoveFileEntryRequest) → CloudEmpty` | Переместить запись (`new_directory_id` пуст = корень) |
| `DeleteFileEntry(DeleteFileEntryRequest) → CloudEmpty` | Удалить запись (`UploadFile` не трогается, декремент `Uploaders` если это была последняя копия владельца) |
| `ListUserImages(ListUserImagesRequest) → ListUserImagesResponse` | **[DEPRECATED]** Все изображения пользователя; используйте `ListUserMedia(PHOTO)`. Исключает превью-блобы |
| `ListUserMedia(ListUserMediaRequest) → ListUserMediaResponse` | Медиа пользователя по типу (`kind` = PHOTO/VIDEO) от новых к старым; cursor-пагинация (`cursor_created_at` + `cursor_file_id`); фильтр по `MediaKind`, исключает превью-блобы |
| `SetVideoThumbnail(SetVideoThumbnailRequest) → CloudEmpty` | Заменить превью видео загруженной картинкой (`video_file_id`, `source_image_file_id`); пересоздаёт `FilePreview` из источника |
| `GetPath(GetPathRequest) → PathResponse` | Построить путь до объекта в иерархии |
| `ListTrash(ListTrashRequest) → ListTrashResponse` | Список файлов в корзине (от свежеудалённых); cursor `(cursor_deleted_at + cursor_entry_id)`; `TrashEntry` содержит `entry`, `file`, `deleted_at`, `purge_at` |
| `RestoreFromTrash(RestoreFromTrashRequest) → CloudEmpty` | Восстановить файл из корзины (в исходную папку либо в корень, если она удалена) |
| `DeleteFromTrash(DeleteFromTrashRequest) → CloudEmpty` | Удалить файл из корзины навсегда (немедленно: БД + альбомы + осиротевший блоб из S3) |
| `EmptyTrash(EmptyTrashRequest) → CloudEmpty` | Очистить корзину владельца целиком |
| `AddFavorite(AddFavoriteRequest) → CloudEmpty` | Добавить файл в избранное (по `file_id`; идемпотентно; только файл владельца) |
| `RemoveFavorite(RemoveFavoriteRequest) → CloudEmpty` | Убрать файл из избранного (идемпотентно) |
| `ListFavorites(ListFavoritesRequest) → ListFavoritesResponse` | Все избранные файлы владельца от новых к старым; cursor `(cursor_favorited_at + cursor_file_id)`; исключает корзину и осиротевшие ссылки |

> **Корзина**: `DeleteFileEntry`/`DeleteDirectory` теперь не удаляют сразу, а помечают записи как удалённые (`IsDeleted`, `DeletedAt`, `PurgeAt = DeletedAt + 14 дней`). Файлы в корзине скрыты из иерархии, галереи и альбомов, но сохраняют квоту. Окончательная зачистка — по `DeleteFromTrash`/`EmptyTrash` или фоновым `TrashCleanupService` (раз в 6 ч). Подробнее — [[modules/backend-files-cloud]].

> **Избранное**: отметка на уровне пользователя в таблице `FavoriteFile` (`OwnerId`+`FileId`, уникальна) — привязка к блобу, а не к записи иерархии, поэтому покрывает и фото/видео из галереи (без `CloudFileEntry`), и файлы/документы из папок. `ListFavorites` отдаёт карточки по `UploadFile` (как галерея). Чистится при удалении из корзины навсегда и при удалении аккаунта.

> **Инвариант «одна директория на файл»**: `CopyFileEntry` удалён, `AttachFile` отказывает (`FileAlreadyAttachedException`), если у владельца уже есть `CloudFileEntry` для этого `FileId`. Уникальный индекс `(OwnerId, FileId)`.

### Messages CloudApi

- `CloudEmpty {}` — пустой ответ
- `DirectoryInfo` — информация о папке
- `FileEntryInfo` — содержит `directory_id` (папка-владелец записи)
- `DirectoryListing { repeated DirectoryInfo subdirs; repeated FileEntryInfo files; }` — листинг для дешёвых UI-сценариев
- `FileEntryDetailed { FileEntryInfo entry; UploadFileInfo file; }` — запись + полная info по блобу
- `DirectoryListingDetailed { repeated DirectoryInfo subdirs; repeated FileEntryDetailed files; }`
- `ListUserImagesRequest { limit; cursor_created_at; cursor_file_id; }` — `limit` clamp 1..200, default 50; курсор exclusive
- `ListUserMediaRequest { MediaKind kind; limit; cursor_created_at; cursor_file_id; }` — `kind` = PHOTO/VIDEO
- `UserImageItem { UploadFileInfo file; entries_count; repeated entry_names; repeated entry_ids; }` — карточка по `UploadFile`; `entries_count` — сколько **живых** записей у владельца, `entry_names` — до 5 имён, `entry_ids` — id живых записей (для rename/delete элемента галереи без листинга каталога)
- `ListUserImagesResponse` / `ListUserMediaResponse { items; next_cursor_created_at; next_cursor_file_id; }` — `next_cursor_*` пуст = страниц больше нет
- `SetVideoThumbnailRequest { video_file_id; source_image_file_id; }`
- `AddFavoriteRequest` / `RemoveFavoriteRequest { file_id; }`
- `ListFavoritesRequest { limit; cursor_favorited_at; cursor_file_id; }` → `ListFavoritesResponse { repeated FavoriteEntry items; next_cursor_favorited_at; next_cursor_file_id; }`
- `FavoriteEntry { UploadFileInfo file; favorited_at; }` — карточка по `UploadFile` (как в галерее) + дата добавления в избранное
- Запросы: `Create/Rename/Move/Delete/ListDirectoryRequest`, `Attach/Rename/Move/DeleteFileEntryRequest`, `GetPathRequest`, `ListUserImagesRequest`, `ListUserMediaRequest`
- `ListDirectoryRequest.directory_id` — `optional string`, пустая/неуказанная = корень
- `PathResponse` — путь до объекта

### MediaKind (proto enum)

`MEDIA_KIND_OTHER=0`, `MEDIA_KIND_PHOTO=1`, `MEDIA_KIND_VIDEO=2`, `MEDIA_KIND_DOCUMENT=3`, `MEDIA_KIND_AUDIO=4`. Заполняется при загрузке по content-type, доступно в `UploadFileInfo.media_kind`. Для видео сервер генерирует превью (кадр на 5-й секунде через FFmpeg) тем же пайплайном, что и для изображений (128/512/1024).

## Сервис: `FilesServerApi` (служебный)

Все RPC реализованы:

| RPC | Назначение |
|-----|-----------|
| `GetFileData(GetFileDataRequest) → GetFileDataResponse` | Информация о загруженном файле |
| `GetFilesData(GetFilesDataRequest) → GetFilesDataResponse` | Информация о нескольких файлах |
| `GetUserStorageInfoServer(GetUserStorageInfoServerRequest) → GetUserStorageInfoResponse` | Storage info (админка) |
| `UploadAvatarServer(UploadAvatarServerRequest) → UploadAvatarServerResponse` | Загрузка аватарки пользователя (служебно) |

## Сервис: `AlbumApi` (клиентский, альбомы фото/видео)

Хост — `AlbumApiService` с `[Authorize(Policy = nameof(TokenType.User))]`. Альбомы **универсальные** (могут содержать и фото, и видео). Один файл может состоять в нескольких альбомах (many-to-many через `AlbumItem`), но при этом — максимум в одной директории.

| RPC | Назначение |
|-----|-----------|
| `CreateAlbum(CreateAlbumRequest) → AlbumInfo` | Создать альбом (имя уникально в рамках владельца) |
| `UpdateAlbum(UpdateAlbumRequest) → AlbumInfo` | Изменить имя/описание/обложку (`optional` поля; `cover_file_id` пуст = сброс) |
| `DeleteAlbum(DeleteAlbumRequest) → CloudEmpty` | Удалить альбом (блобы остаются) |
| `AddItemsToAlbum(AddItemsToAlbumRequest) → CloudEmpty` | Добавить фото/видео (`file_ids`); только медиа владельца, дубли пропускаются; первый файл становится обложкой |
| `RemoveItemsFromAlbum(RemoveItemsFromAlbumRequest) → CloudEmpty` | Убрать элементы; при удалении обложки она переустанавливается на первый оставшийся |
| `ListAlbums(ListAlbumsRequest) → ListAlbumsResponse` | Список альбомов владельца с обложкой/описанием/счётчиком; cursor-пагинация `(cursor_updated_at + cursor_album_id)` |
| `ListAlbumItems(ListAlbumItemsRequest) → ListAlbumItemsResponse` | Содержимое альбома (фото/видео с превью); cursor `(cursor_added_at + cursor_file_id)`; опциональный `kind_filter`; осиротевшие ссылки пропускаются |

### Messages AlbumApi

- `AlbumInfo { id; name; description; cover_file_id; cover_preview_url; items_count; created_at; updated_at; }` — `cover_preview_url` — превью обложки (~512px)
- `CreateAlbumRequest { name; description; }`
- `UpdateAlbumRequest { album_id; optional name; optional description; optional cover_file_id; }`
- `AddItemsToAlbumRequest` / `RemoveItemsFromAlbumRequest { album_id; repeated file_ids; }`
- `ListAlbumsRequest { limit; cursor_updated_at; cursor_album_id; }` → `ListAlbumsResponse { albums; next_cursor_updated_at; next_cursor_album_id; }`
- `ListAlbumItemsRequest { album_id; limit; cursor_added_at; cursor_file_id; optional MediaKind kind_filter; }` → `ListAlbumItemsResponse { items; next_cursor_added_at; next_cursor_file_id; }`
- `AlbumItemEntry { UploadFileInfo file; added_at; }`

## Что отсутствует в proto и коде

- Стикерпаки и стикеры
- Загрузка изображений бейджей и постеров
- Прямая HTTP-стримовая загрузка/скачивание — есть только `FilesController` (без proto-описания)

`UploadFileType`: `Unknown=0`, `UserAvatar=1`, `CloudFile=2` (`CLOUD_FILE = 2` в proto enum).

## Типизированные ошибки

- Локальные: `Exceptions/FileAlreadyUploadedException`, `Exceptions/FileNotUploadedException`
- Общие из [[modules/shared-exceptions]] · Files: `FileNotFoundException`, `NotValidFileIdException`, `CloudAccessDeniedException`, `FileAlreadyAttachedException` (инвариант одной директории), `AlbumNotFoundException`, `AlbumNameConflictException`, `InvalidThumbnailSourceException` (SetVideoThumbnail)

## Связь с инфраструктурой

Все загрузки/скачивания через **MinIO** (S3-совместимое):
- `Infrastructure/S3Uploader.cs`
- `Infrastructure/S3BucketInitializer.cs`
- `Infrastructure/S3BucketRegistry.cs`
- `Configurations/BucketS3Options.cs`

Сжатие изображений — `Services/ImageCompressor.cs`. Превью видео (кадр на 5 с) — `Services/VideoThumbnailExtractor.cs` (FFMpegCore; бинарь ffmpeg/ffprobe копируется в образ из `mwader/static-ffmpeg`, путь через `Ffmpeg:BinaryFolder`, по умолчанию `/usr/local/bin`). Сохранение превью (дедуп+S3+`FilePreview`) — `Services/PreviewPersistenceService.cs`. Очистка временных — `Services/TempFileCleanupService.cs` (background).
