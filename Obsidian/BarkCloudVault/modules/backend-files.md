# Backend — Files

Parent: [[index]] · See also: [[api/files-api]] · [[modules/backend-files-cloud]] · [[modules/shared-queue]]

## Назначение

Сервис файлов. Основные ответственности:
1. **Загрузка/скачивание**: presigned URL в MinIO, квоты, временные файлы, сжатие изображений, превью видео (FFmpeg). **Оригинал хранится как есть** (с 2026-06: HEIC больше НЕ подменяется на JPEG — иначе серверный SHA256 не совпадал с клиентским и ломались дедуп автозагрузки/индикатор «уже в облаке»). Для изображений генерируется **полноразмерный JPEG 90% — `JpegView`** (HEIC через ffmpeg, прочие, **включая сам JPEG**, через ImageSharp): отдельный блоб, связан как превью с `TargetWidth=0` → исключён из галереи и чистится при удалении; file_id хранится в колонке `UploadFile.JpegViewFileId`, отдаётся клиентам как `jpeg_view_file_id/jpeg_view_url` для просмотра, оригинал — для скачивания. ⚠️ С 2026-06 JPEG-оригинал **больше не ссылается сам на себя**: раньше `JpegViewUrl` указывал на `/download/{оригинал}`, но `DownloadFile` отдаёт по прямому id только аватарки и превью-файлы (`IsPreviewFile`) → для JPEG-фото вьювер получал **404**. Теперь для всех изображений создаётся отдельный JpegView-блоб (он зарегистрирован как превью → раздаётся; оригинал остаётся за временными ссылками). Легаси-файлы добирает [[#Services|`LegacyJpegViewBackfillService`]]. **Дедупликация контента по хешу снята** — одинаковые файлы сохраняются как отдельные блобы (хеш пишется для проверок наличия; превью по-прежнему дедуплицируются по SHA256). ⚠️ `LegacyPreviewBackfillService` всё ещё перекодирует HEIC-оригинал в JPEG (старая логика) — новые загрузки он не трогает (у них есть превью), но согласовать с JpegView-подходом стоит отдельным шагом
2. **Облачная иерархия** (NextCloud-подобная): папки + записи о файлах — см. дочернюю заметку [[modules/backend-files-cloud]]
3. **Галерея, альбомы и музыка**: классификация медиа (`MediaKind`), раздельные списки фото/видео (`ListUserMedia`), универсальные альбомы фото/видео (`AlbumApi`), аудиотека и музыкальные плейлисты (`MusicApi`)

## Расположение

`Backend/BarkCloud.Files/`

## Файлы

### Domain
- `UploadFile.cs` — загруженный файл (реальный объект в S3); содержит `MediaKind`, `UploadDeviceName` (имя устройства, с которого блоб загружен в первый раз — читается из `x-device-name` в `GetUploadUrl`)
- `UploadFileType.cs` — enum типов: `Unknown=0`, `UserAvatar=1`, `CloudFile=2`
- `MediaKind.cs` — категория медиа: `Other=0`, `Photo=1`, `Video=2`, `Document=3`, `Audio=4` (заполняется при загрузке по content-type)
- `FileHash.cs` — хеш для дедупликации
- `FilePreview.cs` — связка оригинал→превью (превью хранится как отдельный `UploadFile`)
- `TempFile.cs` — временный файл (TTL)
- `CloudDirectory.cs` — папка в облачной иерархии → [[modules/backend-files-cloud]]
- `CloudFileEntry.cs` — запись о файле в иерархии → [[modules/backend-files-cloud]]
- `Album.cs` — альбом (универсальная коллекция фото/видео): `Name`, `Description`, `CoverFileId`
- `AlbumItem.cs` — привязка файла к альбому (many-to-many). При безвозвратном удалении файла (`TrashPurge`, `UserDeleted`) членство чистится явно, обложки переустанавливаются на первый оставшийся элемент
- `MusicPlaylist.cs` / `MusicPlaylistItem.cs` — пользовательские музыкальные плейлисты: `Name`, `Description`, optional `CoverFileId`, элементы аудиофайлов с ручным `Position`. Собственные плейлисты можно переупорядочивать; получатели приватного шаринга — только слушать
- `MusicPlaylistShareLink.cs` / `MusicPlaylistGrant.cs` — публичная ссылка на музыкальный плейлист и приватный грант доступа пользователю
- `FavoriteFile.cs` — отметка «избранное» на уровне пользователя (`OwnerId`+`FileId`, уникальна). Привязка к блобу, а не к записи иерархии → покрывает и фото/видео из галереи, и файлы/документы из папок
- `ShareLink.cs` — постоянная публичная ссылка на блоб (`OwnerId`, `FileId`, уникальный `Token`, `Name`, `CreatedAt`, `ClickCount`). Названа `ShareLink` (не `FileShare`) во избежание коллизии с `System.IO.FileShare`
- `FileGrant.cs` — приватный грант доступа к блобу конкретному пользователю (`OwnerId`→`RecipientId`→`FileId`, уникальная тройка). Получатель видит файл в «мне доступны», смотрит/скачивает (без редактирования/ре-шаринга); чистится при удалении файла/аккаунта
- `FileMetadata.cs` — метаданные блоба 1:1 к `UploadFile` через `FileId`-PK (24 nullable-поля): GPS (`Latitude`/`Longitude`/`Altitude`), `TakenAt`, `CreatorTool`, камера (`CameraMake`/`CameraModel`/`LensModel`), параметры съёмки (`FocalLengthMm`, `FNumber`, `ExposureTimeSeconds`, `Iso`, `Orientation`, `Flash`), видео (`DurationSeconds`, `VideoCodec`, `AudioCodec`, `Bitrate`, `FrameRate`), документ (`DocumentAuthor`, `DocumentTitle`, `DocumentSubject`, `DocumentPageCount`). Привязка к блобу, а не к пользователю — дедупликация прозрачна
- `FileActivityEvent.cs` / `FileActivityKind.cs` — журнал действий по файлу: владелец, blob `FileId`, optional `EntryId`, актор, тип события, краткое описание, JSON-детали и `CreatedAt`. Пишется для загрузки/привязки, rename/move/delete/restore/purge, избранного, публичных ссылок, приватного шаринга и альбомов

### Host
- `FilesApiService.cs` — клиентский gRPC `FilesApi`
- `FilesServerApiService.cs` — серверный gRPC `FilesServerApi`
- `CloudApiService.cs` — gRPC `CloudApi` (иерархия + галерея `ListUserMedia` + `SetVideoThumbnail`) → [[modules/backend-files-cloud]]
- `AlbumApiService.cs` — gRPC `AlbumApi` (альбомы)
- `MusicApiService.cs` — gRPC `MusicApi`: список аудиотреков, temp-URL трека, CRUD плейлистов, ручной порядок, публичные ссылки и приватные гранты
- `FilesController.cs` — HTTP-контроллер (прямые upload/download)

### Services
- `ImageCompressor.cs` — сжатие изображений (на **SixLabors.ImageSharp**; HEIC/HEIF **не декодирует** — для них см. `HeicImageConverter`)
- `VideoThumbnailExtractor.cs` — извлечение кадра-обложки и размеров видео через FFMpegCore (кадр на 5-й секунде). Метод `ProbeFullAsync` возвращает `VideoProbe` (размеры, длительность, кодеки, битрейт, fps, теги контейнера) — используется и для превью, и для метаданных
- `AudioMetadataExtractor.cs` — извлечение аудиотегов через `ffprobe` (`title`/`artist`/`album`/`track`, длительность) и embedded artwork через `ffmpeg`; при загрузке аудио обложка сохраняется как квадратные превью 128/512 через `ImageCompressor.GenerateSquarePreviewsAsync`
- `FileMetadataExtractor.cs` — извлекатор метаданных под все типы (синглтон):
  - `ExtractFromImage(Stream)` — EXIF IFD0/SubIfd/GPS через **MetadataExtractor** (JPEG/HEIC/PNG/TIFF)
  - `ExtractFromVideo(VideoProbe)` — ffprobe + теги контейнера QuickTime/MP4: дата (`com.apple.quicktime.creationdate`/`creation_time`), GPS (`com.apple.quicktime.location.ISO6709`), устройство (`com.apple.quicktime.make/model/software`). Парсер ISO 6709 для координат
  - `ExtractFromPdf(Stream)` — **UglyToad.PdfPig** (`PdfDocument.Information`: Author/Title/Subject/Producer/Creator/CreationDate/NumberOfPages)
  - `ExtractFromOffice(Stream, contentType)` — **DocumentFormat.OpenXml** для DOCX/XLSX/PPTX: `PackageProperties.Creator/Title/Subject/Created` + `ExtendedFilePropertiesPart` для `Application` и счётчика страниц/слайдов
- `HeicImageConverter.cs` — перекодирование HEIC/HEIF → JPEG через ffmpeg (FFMpegCore, `-frames:v 1 -q:v 2`). Нужен потому, что ImageSharp HEIC не читает, а браузеры HEIC не отображают. Используется в `UploadFile` (отдельное JPEG-представление для размеров/превью/`JpegView`; **оригинал HEIC при этом не трогается и хешируется как есть**) и в `LegacyPreviewBackfillService`
- `PreviewPersistenceService.cs` — сохранение превью (дедуп по SHA256 + S3 + `FilePreview`); общий для загрузки и `SetVideoThumbnail`
- `LegacyPreviewBackfillService.cs` — фоновый разовый бэкафилл при старте контейнера (BackgroundService): находит фото-оригиналы (`MediaKind.Photo`) без превью, перекодирует HEIC→JPEG (замена блоба в S3 под тем же ключом + обновление имени/размера/хеша) и генерирует превью 1024/512/128. Курсор по `Id` по возрастанию; дёшев на повторных стартах (файлы с превью выпадают из выборки). Видео не покрывает
- `AlbumViewBuilder.cs` — сборка `AlbumInfo` (счётчик элементов + URL превью обложки) батчем
- `MusicLibraryService.cs` — бизнес-логика аудиотеки: `ListTracks` по `MediaKind.Audio`, `GetTrackDownloadUrl`, плейлисты, `ResolvePublicPlaylist`, публичные `MusicPlaylistShareLink` и приватные `MusicPlaylistGrant`
- `TempFileCleanupService.cs` — фоновая очистка временных файлов (BackgroundService)
- `TrashPurgeService.cs` — окончательная зачистка корзины: снятие `Uploaders`, удаление из альбомов (`AlbumItems`), избранного (`FavoriteFiles`) и публичных ссылок (`ShareLinks`) владельца, удаление записей. ⚠️ **Превью дедуплицируются по SHA256** (один превью-блоб может быть привязан к нескольким оригиналам через разные строки `FilePreview`), поэтому при снятии `Uploaders` с превью владелец убирается **только если у него не осталось другого (не удаляемого сейчас) оригинала, ссылающегося на тот же превью-блоб** — иначе оставшийся файл лишился бы превью (блоб с пустым `Uploaders` добивается воркером, а строки `FilePreview` чистятся в `PurgeOrphanBlobsAsync` по `PreviewFileId`). Физическое удаление осиротевших блобов вынесено в публичный `PurgeOrphanBlobsAsync` (S3 + хеш + связки `FilePreview` + строка `UploadedFiles`); **строка БД удаляется только при успешном удалении объекта из S3** — иначе блоб остаётся осиротевшим и его добивает воркер (объект не «протекает» в S3). Общий для ручных RPC и воркеров. Константа `Retention = 14 дней`
- `TrashCleanupService.cs` — фоновый воркер (BackgroundService, раз в 6 ч): зачищает записи корзины с истёкшим `PurgeAt` через `TrashPurgeService`
- `OrphanBlobCleanupService.cs` — фоновый воркер (BackgroundService, раз в 6 ч): находит блобы `UploadFile` с пустым `Uploaders` и добивает их через `TrashPurgeService.PurgeOrphanBlobsAsync`. Покрывает пути, которые лишь декрементят `Uploaders` (например удаление аккаунта), и ретраит неудавшиеся S3-удаления
- `LegacyMetadataBackfillService.cs` — фоновый разовый бэкафилл при старте контейнера (BackgroundService) по образцу `LegacyPreviewBackfillService`: находит `UploadFile` (`CloudFile`, с `Etag`) без `FileMetadata` через `IFileMetadataStorage.ListFilesMissingMetadata`, скачивает блоб из S3 во временный файл, прогоняет через нужный extractor по content-type/media-kind (image / video / audio / pdf / office) и сохраняет метаданные. Для аудио использует `AudioMetadataExtractor` (`ffprobe`) — старые треки получают длительность/теги. Курсор по `UploadFile.Id` возрастающий; дёшев на повторных стартах
- `LegacyJpegViewBackfillService.cs` — фоновый разовый бэкафилл при старте (BackgroundService, задержка 45с — после превью- и метаданных-бэкафиллов): находит фото-оригиналы (`MediaKind.Photo`, с `Etag`) **без JpegView-связки** (нет `FilePreview` с `TargetWidth=0`) и не являющиеся сами превью-блобом, скачивает оригинал, перекодирует в полноразмерный JPEG 90% (HEIC через ffmpeg `ConvertToJpegAsync`, прочие через `EncodeFullJpegAsync`), сохраняет через `PersistJpegViewAsync` и проставляет `JpegViewFileId`. **Оригинальный блоб в S3 не трогает** (в отличие от `LegacyPreviewBackfillService`). Покрывает легаси-JPEG, что раньше давали 404 во вьювере. Курсор по `Id` возрастающий; дёшев на повторных стартах
- `FileActivityWriter.cs` — best-effort writer истории: команды не падают, если запись события не удалась; детали сериализуются в `DetailsJson`. В тестах может подставляться `Noop`, чтобы старые unit-тесты хендлеров не требовали нового dependency

### Infrastructure
- `S3BucketInitializer.cs` — создание/проверка бакетов MinIO при старте; временная
  недоступность MinIO (например, `Connection refused` во время запуска контейнера)
  повторяется до 10 попыток с backoff, постоянные ошибки конфигурации не ретраятся
- `S3BucketRegistry.cs` — реестр бакетов
- `S3Uploader.cs` — обёртка над S3/MinIO: `UploadAsync`, `DownloadAsync`, `DeleteAsync` (удаление объекта, идемпотентно — используется зачисткой корзины)
- `PhysicalStorageStatsProvider.cs` — ленивый snapshot диска MinIO: общий размер, занято не-S3, занято S3; кеш 5 минут, обновляется только при запросах storage-info

### Configurations
- `BucketS3Options.cs` — настройки S3-бакета

### Persistence
- `FilesContext.cs`, `FilesContextFactory.cs` — EF Core DbContext (содержит `UploadedFiles`, `FileHashes`, `TempFiles`, `CloudDirectories`, `CloudFileEntries`, `FilePreviews`, `Albums`, `AlbumItems`, `MusicPlaylists`, `MusicPlaylistItems`, `MusicPlaylistShareLinks`, `MusicPlaylistGrants`, `DynamicFolders`, `FavoriteFiles`, `ShareLinks`, `FolderShareLinks`, `FileGrants`, `DirectoryGrants`, `FileMetadata`, `FileActivityEvents`). Миграция `20260602120000_AddUploadedFilesUploadersIndex.cs` — raw-SQL GIN-индекс на массив `UploadedFiles."Uploaders"` (`array_ops`): галерея `ListUserMedia` и подсчёт квоты фильтруют `Uploaders.Contains(ownerId)` → `@>`, ранее seq-scan
- `UploadedFilesStorage.cs`
- `FileHashesStorage.cs`
- `TempFilesStorage.cs`
- `CloudHierarchyStorage.cs` — см. [[modules/backend-files-cloud]]; метод `FileEntryExistsForFile` для инварианта одной директории
- `AlbumStorage.cs` — CRUD альбомов и их элементов, cursor-пагинация
- `FavoriteFilesStorage.cs` — избранное: `Exists`/`Add`/`Remove`/`ListPage` (cursor-пагинация), по образцу item-методов `AlbumStorage`
- `UploadedFilesStorage.cs` — добавлены `ListMemoriesForDay` (фото/видео с `FileMetadata.TakenAt` за месяц+день любых лет, для «Воспоминаний») и `ListMediaWithLocationPage` (медиа с `Latitude/Longitude`, cursor, для карты). Оба фильтруют как `ListUserMediaPage` (живые блобы владельца, не превью, не в корзине) + join к `FileMetadata`. DTO-записи `MemoryMediaItem`/`LocatedMediaItem` объявлены в `IUploadedFilesStorage.cs`. **Отдельных индексов нет**: запросы ведутся через GIN по `Uploaders` + PK `FileMetadata.FileId`
- `ShareStorage.cs` — публичные ссылки: `Add`/`GetByToken`/`Remove` (scoped по владельцу, идемпотентно)/`IncrementClicks`/`ListPage` (cursor-пагинация), по образцу `FavoriteFilesStorage`
- `FileMetadataStorage.cs` — метаданные блоба: `Get`/`AddIfMissing` (идемпотентно, не перезаписывает)/`ListFilesMissingMetadata` (LEFT JOIN-выборка для бэкафилла)
- `FileActivityStorage.cs` — append-only журнал действий: `Add`/`AddRange` и cursor-пагинация `ListPage(ownerId, fileId, cursorCreatedAt, cursorEventId, limit)` по `(CreatedAt desc, Id desc)`
- `Migrations/`:
  - `20260518172338_InitialCreate.cs`
  - `20260518174041_AddCloudDirectories.cs` — добавляет таблицы Cloud
  - `20260518180038_AddFilePreviews.cs` — таблица `FilePreviews`
  - `20260524204149_AddMediaKindAndAlbums.cs` — колонка `MediaKind` (+ бэкафилл), таблицы `Albums`/`AlbumItems`, уникальный индекс `CloudFileEntries(OwnerId, FileId)` с дедупликацией
  - `20260525213058_AddTrashToCloudFileEntries.cs` — корзина: колонки `IsDeleted`/`DeletedAt`/`PurgeAt` в `CloudFileEntries`, частичные уникальные индексы (`WHERE IsDeleted = false`), индекс по `PurgeAt` (`WHERE IsDeleted = true`)
  - `20260525223410_AddFavoriteFiles.cs` — таблица `FavoriteFiles` (уник. индекс `(OwnerId, FileId)`, индекс `(OwnerId, CreatedAt)`)
  - `20260528041219_AddUploadDeviceName.cs` — nullable-колонка `UploadDeviceName` в `UploadedFiles` (имя устройства загрузки)
  - `20260528215548_AddShareLinks.cs` — таблица `ShareLinks` (уник. индекс `Token`, индекс `(OwnerId, CreatedAt)`)
  - `20260530132207_AddFileMetadata.cs` — таблица `FileMetadata` (PK = `FileId`, 24 nullable-колонки)
  - `20260611221205_AddFileActivityEvents.cs` — таблица `FileActivityEvents` + индексы `(OwnerId, CreatedAt)` и `(OwnerId, FileId, CreatedAt)`
  - `20260617120000_AddMusicPlaylistsAndAudioMetadata.cs` — аудиометаданные в `FileMetadata`, таблицы музыкальных плейлистов, публичных ссылок и приватных грантов. Должна быть зарегистрирована через EF-атрибут `[Migration("20260617120000_AddMusicPlaylistsAndAudioMetadata")]`, иначе `Database.Migrate()` не добавит колонки `AudioAlbum`/`AudioArtist`/`AudioTitle`/`AudioTrackNumber`

### Exceptions (локальные)
- `FileAlreadyUploadedException.cs`
- `FileNotUploadedException.cs`

### Consumers
- `SessionRevokedConsumer.cs` — слушает `SessionRevokedEvent` из [[modules/shared-queue]]
- `UserDeletedConsumer.cs` — по `UserDeleted` (из [[modules/backend-users]]) снимает пользователя из `Uploaders` всех его блобов (освобождает квоту) и удаляет его `CloudDirectories`/`CloudFileEntries`/`Albums`/`AlbumItems`/`FavoriteFiles`/`ShareLinks`/`FileGrants` (как владельца и как получателя). Физическое удаление осиротевших S3-блобов делает фоновый `OrphanBlobCleanupService`

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
| `DownloadFile` | Скачивание (через контроллер); для `TempFile`-ссылок отдаёт оригинальный `UploadFile.Filename` в `Content-Disposition`, а не `{fileId}.{ext}` |
| `CheckFileHash` | Проверка наличия по хешу (без побочных эффектов); возвращает `exists` + локации копий пользователя (имя+папка) для модалки «файл уже есть» |
| `CheckFileHashes` | Пакетная проверка наличия по списку SHA256-хешей (без побочных эффектов; для пассивной индикации «в облаке») |
| `GetFileData` / `GetFilesData` | Метаданные файла(ов) |
| `GetFileMetadata` | EXIF/ffprobe/PDF/Office метаданные блоба (для диалога «Свойства»). Только собственные файлы (по `Uploaders`). Возвращает `HasMetadata=false`, если ничего не извлекалось |
| `GetUserStorageInfo` / `GetUserStorageInfoServer` | Информация о квоте + физический snapshot диска MinIO |
| `UploadAvatarServer` | Загрузка аватара пользователя (служебный) |

### Облачная иерархия + галерея (вложенно в `Features/Cloud/`)

`CreateDirectory`, `RenameDirectory`, `MoveDirectory`, `DeleteDirectory`, `ListDirectory`, `ListDirectoryDetailed`, `AttachFile`, `RenameFileEntry`, `MoveFileEntry`, `DeleteFileEntry`, `GetPath`, `ListFileActivity`, `ListUserImages` (deprecated), `ListUserMedia` (фото/видео по `MediaKind`), `DeleteUserMedia` (удаление галерейного `file_id` в корзину; если записи каталога нет — создаёт удалённую запись в системной папке), `SetVideoThumbnail`. **Корзина**: `ListTrash`, `RestoreFromTrash`, `DeleteFromTrash`, `EmptyTrash` (`DeleteFileEntry`/`DeleteDirectory` теперь soft-delete). **Избранное**: `AddFavorite`/`RemoveFavorite` (по `file_id`, идемпотентны), `ListFavorites` (cursor-пагинация, исключает корзину и осиротевшие ссылки). **Публичные ссылки**: `CreateShare` (проверка владения, токен base64url), `ListMyShares` (cursor-пагинация), `RevokeShare` (идемпотентно). `CopyFileEntry` **удалён** (инвариант одной директории). Подробнее — [[modules/backend-files-cloud]].

**История активности** (`ListFileActivity`): read-only RPC для карточки «Свойства». Проверяет, что файл принадлежит владельцу (`Uploaders`), затем отдаёт последние события по blob `file_id` с cursor `(cursor_created_at + cursor_event_id)`. События пишутся синхронно, но best-effort: ошибка аудита логируется и не откатывает пользовательскую операцию.

**Воспоминания / Карта** (вложенно в `Features/Cloud/`): `GetMemories` («В этот день» — фото/видео за сегодняшнюю дату прошлых лет, сгруппированы по году от свежего к старому; группировка в памяти из выборки ≤500, ≤`per_year_limit` превью на год) и `ListMediaLocations` (точки для карты — медиа с GPS, cursor-пагинация; на точку отдаётся узкое превью; клиент кластеризует). Read-only, без миграций — опираются на уже извлечённые `FileMetadata`. См. [[api/files-api]] · `CloudApi`.

`ResolveShare` (публичный резолв токена) — в служебном `FilesServerApi` (`Features/Cloud/ResolveShare/`, без `UserContext`): по токену отдаёт `download_url` через `FileUrlHelper` и инкрементит `ClickCount`. Зовётся из Web-роута `/s/{token}`.

### Альбомы (вложенно в `Features/Album/`)

7 фич: `CreateAlbum`, `UpdateAlbum`, `DeleteAlbum`, `AddItemsToAlbum`, `RemoveItemsFromAlbum`, `ListAlbums`, `ListAlbumItems`. См. [[api/files-api]] · `AlbumApi`.

> **Не реализовано**: стикеры, бейджи, постеры — нет ни в `Features/`, ни в `Shared/BarkCloud.Proto/files_api.proto`.

### Музыка (`MusicApi`)

`MusicApiService` опирается на `MusicLibraryService`, а не на MediatR-slices. Аудиофайлы определяются по `MediaKind.Audio`; при загрузке сервер вытаскивает теги/длительность и embedded artwork, а для обложки создаёт превью 128/512. Треки возвращаются с temp-URL оригинала и URL обложек. Плейлисты владельца приватные по умолчанию, поддерживают ручной порядок только для владельца (`can_reorder=true`), кастомную обложку из фото и динамическую fallback-обложку по первому треку. Доступ: публичный токен (`ResolveMusicPlaylistShare`, страница `/mpl/{token}`) или приватный грант пользователю (`ListSharedPlaylistsWithMe`).

## Зависимости

- Использует: `BarkCloud.Proto`, `BarkCloud.GrpcServer`, `BarkCloud.Shared.*`, EF Core, PostgreSQL, MinIO (S3 SDK), RabbitMQ, MediatR, **SixLabors.ImageSharp** (превью изображений), **FFMpegCore** (превью видео), **MetadataExtractor** (EXIF фото), **UglyToad.PdfPig** (метаданные PDF), **DocumentFormat.OpenXml** (метаданные DOCX/XLSX/PPTX)
- Образ Files содержит бинарь `ffmpeg`/`ffprobe` (COPY из `mwader/static-ffmpeg` в `Dockerfile`/`Dockerfile.slim`)
- В Dockerfile Files publish-артефакты и `ffmpeg`/`ffprobe` раскладываются по отдельным слоям (`runtimes`, `logs`, root publish, оба бинаря), чтобы push через registry не упирался в лимит размера одного upload-запроса.
- Runtime-образ Files — `mcr.microsoft.com/dotnet/aspnet:10.0-noble` с установленным `libgssapi-krb5-2`: Npgsql/EF при миграциях может загружать GSSAPI, а `noble-chiseled` не содержит `libgssapi_krb5.so.2`

## Единый поиск

`SearchApiService` вызывает `UnifiedSearchService` для всех личных разделов поиска. Сервис нормализует Unicode NFKC, пробелы и регистр, классифицирует один файл ровно в одну основную группу (`Фото`/`Видео`/`Музыка`/`Файлы`), ранжирует exact → prefix → substring → trigram-like typo и выдаёт opaque keyset-курсор. `ResolveHit` повторно авторизует deeplink.

Личные метаданные поиска лежат в `FileSearchAliases` и `FileTags`: ключ включает `OwnerId`, поэтому алиасы и теги не уходят получателю shared-доступа. `ReplaceFileSearchMetadata` атомарно заменяет один алиас (≤120) и до 20 тегов (≤50); `TrashPurgeService` и `UserDeletedConsumer` явно чистят строки. Миграция `20260906150936_AddFileSearchMetadata` включает `pg_trgm` и GIN-индексы для новых таблиц и создаёт trigram-индексы крупных именных таблиц concurrently.
- Тесно связан с MinIO (см. [[structure/infrastructure]])
