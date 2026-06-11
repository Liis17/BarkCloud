# gRPC API — Files

Parent: [[index]] · Module: [[modules/backend-files]] · Cloud: [[modules/backend-files-cloud]] · Proto: [[modules/shared-proto]] · Клиентский гайд: [[api/files-client-guide]]

Файл: `Shared/BarkCloud.Proto/files_api.proto`
Namespace C#: `BarkCloud.Proto.Files`
Package: `barkcloud.files`

В proto-файле определены сервисы: `FilesApi` (клиент), `CloudApi` (клиент, облачная иерархия + галерея), `DynamicFolderApi` (клиент, умные папки), `FilesServerApi` (служебный), `AlbumApi` (клиент, альбомы фото/видео).

## Сервис: `FilesApi` (клиентский)

Все RPC реализованы:

| RPC | Назначение |
|-----|-----------|
| `GetUploadUrl(GetUploadUrlRequest) → GetUploadUrlResponse` | Получить presigned URL для загрузки |
| `GetTempDownloadUrl(GetTempDownloadUrlRequest) → GetTempDownloadUrlResponse` | Ссылки на скачивание + превью (`file_id`, `url`, `preview_url`) |
| `CheckFileHash(CheckFileHashRequest) → CheckFileHashResponse` | Проверка наличия по хешу (без побочных эффектов): `exists` + `existing_locations` (имя+папка) для модалки «файл уже есть» |
| `GetUserStorageInfo(GetUserStorageInfoRequest) → GetUserStorageInfoResponse` | Инфо о квоте/использовании + физический snapshot диска |
| `GetFileMetadata(GetFileMetadataRequest) → GetFileMetadataResponse` | Метаданные файла (EXIF/ffprobe/PDF/Office) для диалога «Свойства». Только для собственных файлов (по `Uploaders`). Поля nullable (`optional`), клиент показывает только заданные. `has_metadata=false` если для блоба не извлечено ни одного поля |

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
| `AttachFile(AttachFileRequest) → CloudEmpty` | Привязать загруженный `UploadFile` к папке (создаёт `CloudFileEntry`); коллизия имени → суффикс ` (1)`. `route_by_media_kind=true` → `directory_id` игнорируется, файл кладётся по типу в системную папку Фото/Видео/Другие документы |
| `RenameFileEntry(RenameFileEntryRequest) → CloudEmpty` | Переименовать запись (не меняет `UploadFile.Filename`) |
| `MoveFileEntry(MoveFileEntryRequest) → CloudEmpty` | Переместить запись (`new_directory_id` пуст = корень) |
| `DeleteFileEntry(DeleteFileEntryRequest) → CloudEmpty` | Удалить запись в корзину (`UploadFile`/`Uploaders` не трогает; blob удаляется только при очистке корзины) |
| `DeleteFileEntries(DeleteFileEntriesRequest) → DeleteFileEntriesResponse` | Массово переместить записи в корзину; чужие/несуществующие/уже удалённые id пропускаются, ответ содержит `deleted_count` |
| `ListUserImages(ListUserImagesRequest) → ListUserImagesResponse` | **[DEPRECATED]** Все изображения пользователя; используйте `ListUserMedia(PHOTO)`. Исключает превью-блобы |
| `ListUserMedia(ListUserMediaRequest) → ListUserMediaResponse` | Медиа пользователя по типу (`kind` = PHOTO/VIDEO) от новых к старым; cursor-пагинация (`cursor_created_at` + `cursor_file_id`); фильтр по `MediaKind`, исключает превью-блобы |
| `DeleteUserMedia(DeleteUserMediaRequest) → CloudEmpty` | Удалить медиа из галереи по `file_id`: живые записи каталога перемещает в корзину; если записей нет — создаёт запись корзины в системной папке по типу медиа |
| `SetVideoThumbnail(SetVideoThumbnailRequest) → CloudEmpty` | Заменить превью видео загруженной картинкой (`video_file_id`, `source_image_file_id`); пересоздаёт `FilePreview` из источника |
| `GetMemories(GetMemoriesRequest) → GetMemoriesResponse` | «Воспоминания — В этот день»: фото/видео за указанный (или сегодняшний UTC) месяц+день прошлых лет по `FileMetadata.TakenAt`, группы-годы (`MemoryGroup { year; years_ago; total_count; items }`) от свежего к старому; ≤`per_year_limit` превью на год |
| `ListMediaLocations(ListMediaLocationsRequest) → ListMediaLocationsResponse` | Точки для карты: медиа с GPS (`MediaLocationPoint { file_id; latitude; longitude; media_kind; preview_url; taken_at?; created_at }`), cursor-пагинация (`cursor_created_at`+`cursor_file_id`); клиент кластеризует |
| `GetPath(GetPathRequest) → PathResponse` | Построить путь до объекта в иерархии |
| `ListFileActivity(ListFileActivityRequest) → ListFileActivityResponse` | История действий по файлу для «Свойства»: последние события по `file_id`, cursor-пагинация (`cursor_created_at` + `cursor_event_id`), доступ только владельцу blob |
| `ListTrash(ListTrashRequest) → ListTrashResponse` | Список файлов в корзине (от свежеудалённых); cursor `(cursor_deleted_at + cursor_entry_id)`; `TrashEntry` содержит `entry`, `file`, `deleted_at`, `purge_at` |
| `GetTrashSummary(GetTrashSummaryRequest) → GetTrashSummaryResponse` | Лёгкая сводка: `total_count` + `oldest_purge_at` (серверный `COUNT` + `MIN(PurgeAt)`). Для бейджей/виджета корзины — «самый истекающий» файл без выгрузки страниц |
| `RestoreFromTrash(RestoreFromTrashRequest) → CloudEmpty` | Восстановить файл из корзины (в исходную папку либо в корень, если она удалена) |
| `DeleteFromTrash(DeleteFromTrashRequest) → CloudEmpty` | Удалить файл из корзины навсегда (немедленно: БД + альбомы + осиротевший блоб из S3) |
| `EmptyTrash(EmptyTrashRequest) → CloudEmpty` | Очистить корзину владельца целиком |
| `AddFavorite(AddFavoriteRequest) → CloudEmpty` | Добавить файл в избранное (по `file_id`; идемпотентно; только файл владельца) |
| `RemoveFavorite(RemoveFavoriteRequest) → CloudEmpty` | Убрать файл из избранного (идемпотентно) |
| `ListFavorites(ListFavoritesRequest) → ListFavoritesResponse` | Все избранные файлы владельца от новых к старым; cursor `(cursor_favorited_at + cursor_file_id)`; исключает корзину и осиротевшие ссылки |
| `CreateShare(CreateShareRequest) → ShareInfo` | Создать постоянную публичную ссылку на файл владельца (проверка владения по `Uploaders`; токен — base64url из 16 случайных байт) |
| `ListMyShares(ListMySharesRequest) → ListMySharesResponse` | Публичные ссылки владельца от новых к старым; cursor `(cursor_created_at + cursor_share_id)` |
| `RevokeShare(RevokeShareRequest) → CloudEmpty` | Отозвать ссылку (идемпотентно, scoped по владельцу) |

> **Корзина**: `DeleteFileEntry`/`DeleteDirectory`/`DeleteUserMedia` не удаляют blob сразу, а помечают или создают записи как удалённые (`IsDeleted`, `DeletedAt`, `PurgeAt = DeletedAt + 14 дней`). Файлы в корзине скрыты из иерархии, галереи и альбомов, но сохраняют квоту. Окончательная зачистка — по `DeleteFromTrash`/`EmptyTrash` или фоновым `TrashCleanupService` (раз в 6 ч). Подробнее — [[modules/backend-files-cloud]].

> **Избранное**: отметка на уровне пользователя в таблице `FavoriteFile` (`OwnerId`+`FileId`, уникальна) — привязка к блобу, а не к записи иерархии, поэтому покрывает и фото/видео из галереи (без `CloudFileEntry`), и файлы/документы из папок. `ListFavorites` отдаёт карточки по `UploadFile` (как галерея). Чистится при удалении из корзины навсегда и при удалении аккаунта.

> **Инвариант «одна директория на файл»**: `CopyFileEntry` удалён, `AttachFile` отказывает (`FileAlreadyAttachedException`), если у владельца уже есть `CloudFileEntry` для этого `FileId`. Уникальный индекс `(OwnerId, FileId)`.

> **Публичные папки (динамическая страница)**: сущность `FolderShareLink` (владелец→папка, уникальный `Token`, уникальность `(OwnerId, DirectoryId)`). RPC `CreateFolderShare` (идемпотентно), `ListMyFolderShares`, `RevokeFolderShare` (CloudApi, владелец) и `ResolveFolderShare(token, dir)` (FilesServerApi, анонимно). Резолв динамический: по токену отдаёт листинг текущей папки (`dir` валидируется принадлежностью к поддереву через `GetSubtree`) — подпапки + файлы с публичными temp-URL и URL превью; отдельные `ShareLink` на файлы НЕ создаются, добавленные позже файлы видны сразу. `RevokeFolderShare` каскадно снимает все отдельные `ShareLink` на файлы поддерева (`IShareStorage.RemoveByFiles`) — папка снова приватна (в т.ч. индивидуально опубликованные файлы).

> **Публичные альбомы (динамическая страница)**: сущность `AlbumShareLink` (владелец→альбом, уникальный `Token`, уникальность `(OwnerId, AlbumId)`). RPC `CreateAlbumShare` (идемпотентно), `ListMyAlbumShares`, `RevokeAlbumShare` (CloudApi, владелец) и `ResolveAlbumShare(token, cursor)` (FilesServerApi, анонимно). Резолв динамический: по токену отдаёт элементы альбома (cursor-пагинация) с публичными temp-URL и URL превью, исключая «эффективно удалённые». Зеркало публичных папок; `DeleteAlbum` снимает публичность. Страница `/al/{token}`.

> **Поиск по имени**: `SearchFiles(query, limit, cursor, kind_filter)` (CloudApi, владелец) → `SearchFilesResponse { files[FileEntryDetailed], next_cursor_created_at, next_cursor_entry_id }`. Подстрока имени по живым `CloudFileEntry` владельца (по всему облаку), `(CreatedAt desc, Id desc)`, обогащение как `ListDirectoryDetailed`. `kind_filter` (repeated MediaKind, пусто = все типы) — фильтр по типу медиа файла, применяется в SQL до `Take(limit+1)` (limit/cursor честные).

> **Шаринг между пользователями**: приватные гранты `FileGrant` (владелец→получатель→файл). RPC `ShareFileWithUser`/`RevokeUserShare`/`ListMyOutgoingShares` (с кем поделён один файл)/`ListMyOutgoingSharesAll` (все мои исходящие гранты — «я поделился», плоский список с `UploadFileInfo`+получателем, cursor-пагинация по `CreatedAt desc, Id desc`; группировку по файлу делает веб) и `ListSharedWithMe`/`GetSharedFileDownloadUrl` (получатель — доступ строго по гранту через `TempFile`, без обхода `DownloadFileCommandHandler`). Имена резолвит веб-слой через Users (`UsersServerApi.ListByIds`). Публичный `ResolveShare` дополнительно отдаёт `media_kind`/`preview_url`/размеры для страницы просмотра. Гранты чистятся в `UserDeleted`/`TrashPurge`.

> **Шаринг папки между пользователями**: гранты `DirectoryGrant` (владелец→получатель→папка, рекурсивно). RPC `ShareFolderWithUser`/`RevokeFolderUserShare`/`ListMyOutgoingFolderShares` (владелец, «я поделился» — папки)/`ListSharedFoldersWithMe` (получатель, «мне доступны» — папки)/`ListSharedDirectory(directory_id)` (навигация по доступному поддереву — listing с публичными temp-URL и превью). Доступ получателя считает `FolderGrantAccessService`: файл/папка доступны, если входят в поддерево любого гранта получателя (через `GetSubtree`). Этим же сервисом расширен `GetSharedFileDownloadUrl` (доступ по прямому гранту ИЛИ через папку). Гранты папок чистятся при удалении папки (`DeleteDirectory`) и аккаунта (`UserDeleted`).

### Messages CloudApi

- `CloudEmpty {}` — пустой ответ
- `DirectoryInfo` — информация о папке
- `FileEntryInfo` — содержит `directory_id` (папка-владелец записи)
- `DirectoryListing { repeated DirectoryInfo subdirs; repeated FileEntryInfo files; }` — листинг для дешёвых UI-сценариев
- `FileEntryDetailed { FileEntryInfo entry; UploadFileInfo file; }` — запись + полная info по блобу
- `DirectoryListingDetailed { repeated DirectoryInfo subdirs; repeated FileEntryDetailed files; }`
- `ListUserImagesRequest { limit; cursor_created_at; cursor_file_id; }` — `limit` clamp 1..200, default 50; курсор exclusive
- `ListUserMediaRequest { MediaKind kind; limit; cursor_created_at; cursor_file_id; }` — `kind` = PHOTO/VIDEO
- `UserImageItem { UploadFileInfo file; entries_count; repeated entry_names; repeated entry_ids; duplicate_group_key; }` — карточка по `UploadFile`; `entries_count` — сколько **живых** записей у владельца, `entry_names` — до 5 имён, `entry_ids` — id живых записей (для rename/перехода к элементу галереи без листинга каталога; удаление галереи идёт через `DeleteUserMedia(file_id)`), `duplicate_group_key` — SHA-256 группы для системных папок дубликатов
- `ListUserImagesResponse` / `ListUserMediaResponse { items; next_cursor_created_at; next_cursor_file_id; }` — `next_cursor_*` пуст = страниц больше нет
- `SetVideoThumbnailRequest { video_file_id; source_image_file_id; }`
- `AddFavoriteRequest` / `RemoveFavoriteRequest { file_id; }`
- `ListFavoritesRequest { limit; cursor_favorited_at; cursor_file_id; }` → `ListFavoritesResponse { repeated FavoriteEntry items; next_cursor_favorited_at; next_cursor_file_id; }`
- `FavoriteEntry { UploadFileInfo file; favorited_at; }` — карточка по `UploadFile` (как в галерее) + дата добавления в избранное
- `ShareInfo { id; token; file_id; name; created_at; click_count; media_kind; preview_url; }` — публичная ссылка; `token` — часть дружелюбного URL `/s/{token}`. `media_kind`/`preview_url` (минимальное превью, target 128) заполняет `ListMyShares` батчем — для карточек «Мои публичные» в вебе; `CreateShare` отдаёт их пустыми
- `CreateShareRequest { file_id; name; }`
- `ListMySharesRequest { limit; cursor_created_at; cursor_share_id; }` → `ListMySharesResponse { repeated ShareInfo shares; next_cursor_created_at; next_cursor_share_id; }`
- `RevokeShareRequest { share_id; }`
- Запросы: `Create/Rename/Move/Delete/ListDirectoryRequest`, `Attach/Rename/Move/DeleteFileEntryRequest`, `DeleteUserMediaRequest`, `GetPathRequest`, `ListUserImagesRequest`, `ListUserMediaRequest`
- `ListDirectoryRequest.directory_id` — `optional string`, пустая/неуказанная = корень
- `PathResponse` — путь до объекта
- `ListFileActivityRequest { file_id; limit; cursor_created_at; cursor_event_id; }` → `ListFileActivityResponse { repeated FileActivityInfo items; next_cursor_created_at; next_cursor_event_id; }`
- `FileActivityInfo { id; file_id; entry_id; actor_user_id; kind; summary; details_json; created_at; }` — append-only событие активности. `kind` — строковый код (`uploaded`, `attached`, `renamed`, `moved`, `deleted`, `restored`, `purged`, `favorite_added`, `favorite_removed`, `share_created`, `share_revoked`, `shared_with_user`, `user_share_revoked`, `album_added`, `album_removed`)

### UploadFileInfo · поле upload_device_name

`UploadFileInfo.upload_device_name` (поле 15) — имя устройства, с которого блоб был загружен. Сервер заполняет его в `GetUploadUrl` из gRPC-заголовка `x-device-name` ([[modules/shared-auth]] · `RequestContext.DeviceName`); хранится на блобе (`UploadFile.UploadDeviceName`). Каждая загрузка сохраняется как отдельный блоб (дедупликация контента по хешу снята), поэтому значение принадлежит конкретной загрузке. В веб-клиенте выводится в модалке «Свойства» (`/api/files/info` → `uploadDeviceName`).

### MediaKind (proto enum)

`MEDIA_KIND_OTHER=0`, `MEDIA_KIND_PHOTO=1`, `MEDIA_KIND_VIDEO=2`, `MEDIA_KIND_DOCUMENT=3`, `MEDIA_KIND_AUDIO=4`. Заполняется при загрузке по content-type, доступно в `UploadFileInfo.media_kind`. Для видео сервер генерирует превью (кадр на 5-й секунде через FFmpeg) тем же пайплайном, что и для изображений (128/512/1024).

## Сервис: `DynamicFolderApi` (клиентский, умные папки)

Хост — `DynamicFolderApiService` с пользовательской авторизацией. Системные папки виртуальные и приходят первыми в `ListDynamicFolders`. `sys-duplicate-media` и `sys-duplicate-files` вычисляются отдельно от JSON-критериев: сервер группирует живые файлы владельца по `FileHashes.Hash`, берёт только группы с количеством больше одного и отдаёт `UserImageItem.duplicate_group_key`. `sys-duplicate-media` включает только фото/видео, `sys-duplicate-files` — документы/аудио/прочие файлы.

| RPC | Назначение |
|-----|-----------|
| `CreateDynamicFolder(CreateDynamicFolderRequest) → DynamicFolderInfo` | Создать пользовательскую умную папку |
| `UpdateDynamicFolder(UpdateDynamicFolderRequest) → DynamicFolderInfo` | Обновить пользовательскую умную папку |
| `DeleteDynamicFolder(DeleteDynamicFolderRequest) → CloudEmpty` | Удалить пользовательскую умную папку |
| `ListDynamicFolders(ListDynamicFoldersRequest) → ListDynamicFoldersResponse` | Системные + пользовательские папки с count/cover |
| `ListDynamicFolderItems(ListDynamicFolderItemsRequest) → ListDynamicFolderItemsResponse` | Содержимое папки, cursor `(cursor_created_at + cursor_file_id)`, опциональный `kind_filter` |

## Сервис: `FilesServerApi` (служебный)

Все RPC реализованы:

| RPC | Назначение |
|-----|-----------|
| `GetFileData(GetFileDataRequest) → GetFileDataResponse` | Информация о загруженном файле |
| `GetFilesData(GetFilesDataRequest) → GetFilesDataResponse` | Информация о нескольких файлах |
| `GetUserStorageInfoServer(GetUserStorageInfoServerRequest) → GetUserStorageInfoResponse` | Storage info (админка) + физический snapshot диска |
| `UploadAvatarServer(UploadAvatarServerRequest) → UploadAvatarServerResponse` | Загрузка аватарки пользователя (служебно) |
| `ResolveShare(ResolveShareRequest) → ResolveShareResponse` | Резолв публичного токена (без `UserContext`): `found` + `file_id`/`name`/`download_url`. Внутри создаёт `TempFile` для оригинала (прямой `/download/{fileId}` для `CloudFile` запрещён в `DownloadFileCommandHandler`) и инкрементит `click_count`. Зовётся из Web-роута `/s/{token}` сервисным токеном |

Messages: `ResolveShareRequest { token; }` → `ResolveShareResponse { found; file_id; name; download_url; media_kind; preview_url; image_width; image_height; file_size; }`.

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

`GetUserStorageInfoResponse` дополнительно отдаёт физические показатели mount'а MinIO:
`total_available_storage` (общий размер диска), `disk_used_storage` (занято на диске без S3-данных),
`s3_used_storage` (размер данных S3 на диске). Snapshot считает `PhysicalStorageStatsProvider`,
кеширует результат на 5 минут и обновляет его только при запросе storage-info endpoint'ов.

Сжатие изображений — `Services/ImageCompressor.cs`. Превью видео (кадр на 5 с) — `Services/VideoThumbnailExtractor.cs` (FFMpegCore; бинарь ffmpeg/ffprobe копируется в образ из `mwader/static-ffmpeg`, путь через `Ffmpeg:BinaryFolder`, по умолчанию `/usr/local/bin`). Сохранение превью (дедуп+S3+`FilePreview`) — `Services/PreviewPersistenceService.cs`. Очистка временных — `Services/TempFileCleanupService.cs` (background).
