# Backend — Files

Parent: [[index]] · See also: [[api/files-api]] · [[modules/backend-files-cloud]] · [[modules/shared-queue]]

## Назначение

Сервис файлов. Основные ответственности:
1. **Загрузка/скачивание**: presigned URL в MinIO, квоты, временные файлы, сжатие изображений, превью видео (FFmpeg), перекодирование HEIC→JPEG (FFmpeg) при загрузке + разовый бэкафилл превью для легаси-файлов при старте. **Дедупликация контента по хешу снята** — одинаковые файлы сохраняются как отдельные блобы (хеш пишется для проверок наличия; превью по-прежнему дедуплицируются по SHA256)
2. **Облачная иерархия** (NextCloud-подобная): папки + записи о файлах — см. дочернюю заметку [[modules/backend-files-cloud]]
3. **Галерея и альбомы**: классификация медиа (`MediaKind`), раздельные списки фото/видео (`ListUserMedia`), универсальные альбомы фото/видео (`AlbumApi`)

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
- `AlbumItem.cs` — привязка файла к альбому (many-to-many). При безвозвратном удалении файла (`DeleteUserMedia` hard-delete, `TrashPurge`, `UserDeleted`) членство чистится явно, обложки переустанавливаются на первый оставшийся элемент
- `FavoriteFile.cs` — отметка «избранное» на уровне пользователя (`OwnerId`+`FileId`, уникальна). Привязка к блобу, а не к записи иерархии → покрывает и фото/видео из галереи, и файлы/документы из папок
- `ShareLink.cs` — постоянная публичная ссылка на блоб (`OwnerId`, `FileId`, уникальный `Token`, `Name`, `CreatedAt`, `ClickCount`). Названа `ShareLink` (не `FileShare`) во избежание коллизии с `System.IO.FileShare`
- `FileGrant.cs` — приватный грант доступа к блобу конкретному пользователю (`OwnerId`→`RecipientId`→`FileId`, уникальная тройка). Получатель видит файл в «мне доступны», смотрит/скачивает (без редактирования/ре-шаринга); чистится при удалении файла/аккаунта
- `FileMetadata.cs` — метаданные блоба 1:1 к `UploadFile` через `FileId`-PK (24 nullable-поля): GPS (`Latitude`/`Longitude`/`Altitude`), `TakenAt`, `CreatorTool`, камера (`CameraMake`/`CameraModel`/`LensModel`), параметры съёмки (`FocalLengthMm`, `FNumber`, `ExposureTimeSeconds`, `Iso`, `Orientation`, `Flash`), видео (`DurationSeconds`, `VideoCodec`, `AudioCodec`, `Bitrate`, `FrameRate`), документ (`DocumentAuthor`, `DocumentTitle`, `DocumentSubject`, `DocumentPageCount`). Привязка к блобу, а не к пользователю — дедупликация прозрачна

### Host
- `FilesApiService.cs` — клиентский gRPC `FilesApi`
- `FilesServerApiService.cs` — серверный gRPC `FilesServerApi`
- `CloudApiService.cs` — gRPC `CloudApi` (иерархия + галерея `ListUserMedia` + `SetVideoThumbnail`) → [[modules/backend-files-cloud]]
- `AlbumApiService.cs` — gRPC `AlbumApi` (альбомы)
- `FilesController.cs` — HTTP-контроллер (прямые upload/download)

### Services
- `ImageCompressor.cs` — сжатие изображений (на **SixLabors.ImageSharp**; HEIC/HEIF **не декодирует** — для них см. `HeicImageConverter`)
- `VideoThumbnailExtractor.cs` — извлечение кадра-обложки и размеров видео через FFMpegCore (кадр на 5-й секунде). Метод `ProbeFullAsync` возвращает `VideoProbe` (размеры, длительность, кодеки, битрейт, fps, теги контейнера) — используется и для превью, и для метаданных
- `FileMetadataExtractor.cs` — извлекатор метаданных под все типы (синглтон):
  - `ExtractFromImage(Stream)` — EXIF IFD0/SubIfd/GPS через **MetadataExtractor** (JPEG/HEIC/PNG/TIFF)
  - `ExtractFromVideo(VideoProbe)` — ffprobe + теги контейнера QuickTime/MP4: дата (`com.apple.quicktime.creationdate`/`creation_time`), GPS (`com.apple.quicktime.location.ISO6709`), устройство (`com.apple.quicktime.make/model/software`). Парсер ISO 6709 для координат
  - `ExtractFromPdf(Stream)` — **UglyToad.PdfPig** (`PdfDocument.Information`: Author/Title/Subject/Producer/Creator/CreationDate/NumberOfPages)
  - `ExtractFromOffice(Stream, contentType)` — **DocumentFormat.OpenXml** для DOCX/XLSX/PPTX: `PackageProperties.Creator/Title/Subject/Created` + `ExtendedFilePropertiesPart` для `Application` и счётчика страниц/слайдов
- `HeicImageConverter.cs` — перекодирование HEIC/HEIF → JPEG через ffmpeg (FFMpegCore, `-frames:v 1 -q:v 2`). Нужен потому, что ImageSharp HEIC не читает, а браузеры HEIC не отображают. Используется в `UploadFile` (конвертация оригинала до хеширования) и в `LegacyPreviewBackfillService`
- `PreviewPersistenceService.cs` — сохранение превью (дедуп по SHA256 + S3 + `FilePreview`); общий для загрузки и `SetVideoThumbnail`
- `LegacyPreviewBackfillService.cs` — фоновый разовый бэкафилл при старте контейнера (BackgroundService): находит фото-оригиналы (`MediaKind.Photo`) без превью, перекодирует HEIC→JPEG (замена блоба в S3 под тем же ключом + обновление имени/размера/хеша) и генерирует превью 1024/512/128. Курсор по `Id` по возрастанию; дёшев на повторных стартах (файлы с превью выпадают из выборки). Видео не покрывает
- `AlbumViewBuilder.cs` — сборка `AlbumInfo` (счётчик элементов + URL превью обложки) батчем
- `TempFileCleanupService.cs` — фоновая очистка временных файлов (BackgroundService)
- `TrashPurgeService.cs` — окончательная зачистка корзины: снятие `Uploaders`, удаление из альбомов (`AlbumItems`), избранного (`FavoriteFiles`) и публичных ссылок (`ShareLinks`) владельца, удаление записей. Физическое удаление осиротевших блобов вынесено в публичный `PurgeOrphanBlobsAsync` (S3 + хеш + связки `FilePreview` + строка `UploadedFiles`); **строка БД удаляется только при успешном удалении объекта из S3** — иначе блоб остаётся осиротевшим и его добивает воркер (объект не «протекает» в S3). Общий для ручных RPC и воркеров. Константа `Retention = 14 дней`
- `TrashCleanupService.cs` — фоновый воркер (BackgroundService, раз в 6 ч): зачищает записи корзины с истёкшим `PurgeAt` через `TrashPurgeService`
- `OrphanBlobCleanupService.cs` — фоновый воркер (BackgroundService, раз в 6 ч): находит блобы `UploadFile` с пустым `Uploaders` и добивает их через `TrashPurgeService.PurgeOrphanBlobsAsync`. Покрывает пути, которые лишь декрементят `Uploaders` (удаление аккаунта, удаление медиа из галереи), и ретраит неудавшиеся S3-удаления
- `LegacyMetadataBackfillService.cs` — фоновый разовый бэкафилл при старте контейнера (BackgroundService) по образцу `LegacyPreviewBackfillService`: находит `UploadFile` (`CloudFile`, с `Etag`) без `FileMetadata` через `IFileMetadataStorage.ListFilesMissingMetadata`, скачивает блоб из S3 во временный файл, прогоняет через нужный `ExtractFromX` по content-type (image / video / pdf / office) и сохраняет метаданные. Курсор по `UploadFile.Id` возрастающий; дёшев на повторных стартах

### Infrastructure
- `S3BucketInitializer.cs` — создание/проверка бакетов MinIO при старте
- `S3BucketRegistry.cs` — реестр бакетов
- `S3Uploader.cs` — обёртка над S3/MinIO: `UploadAsync`, `DownloadAsync`, `DeleteAsync` (удаление объекта, идемпотентно — используется зачисткой корзины)

### Configurations
- `BucketS3Options.cs` — настройки S3-бакета

### Persistence
- `FilesContext.cs`, `FilesContextFactory.cs` — EF Core DbContext (содержит `UploadedFiles`, `FileHashes`, `TempFiles`, `CloudDirectories`, `CloudFileEntries`, `FilePreviews`, `Albums`, `AlbumItems`, `FavoriteFiles`, `ShareLinks`)
- `UploadedFilesStorage.cs`
- `FileHashesStorage.cs`
- `TempFilesStorage.cs`
- `CloudHierarchyStorage.cs` — см. [[modules/backend-files-cloud]]; метод `FileEntryExistsForFile` для инварианта одной директории
- `AlbumStorage.cs` — CRUD альбомов и их элементов, cursor-пагинация
- `FavoriteFilesStorage.cs` — избранное: `Exists`/`Add`/`Remove`/`ListPage` (cursor-пагинация), по образцу item-методов `AlbumStorage`
- `ShareStorage.cs` — публичные ссылки: `Add`/`GetByToken`/`Remove` (scoped по владельцу, идемпотентно)/`IncrementClicks`/`ListPage` (cursor-пагинация), по образцу `FavoriteFilesStorage`
- `FileMetadataStorage.cs` — метаданные блоба: `Get`/`AddIfMissing` (идемпотентно, не перезаписывает)/`ListFilesMissingMetadata` (LEFT JOIN-выборка для бэкафилла)
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
| `DownloadFile` | Скачивание (через контроллер) |
| `CheckFileHash` | Проверка наличия по хешу (без побочных эффектов); возвращает `exists` + локации копий пользователя (имя+папка) для модалки «файл уже есть» |
| `CheckFileHashes` | Пакетная проверка наличия по списку SHA256-хешей (без побочных эффектов; для пассивной индикации «в облаке») |
| `GetFileData` / `GetFilesData` | Метаданные файла(ов) |
| `GetFileMetadata` | EXIF/ffprobe/PDF/Office метаданные блоба (для диалога «Свойства»). Только собственные файлы (по `Uploaders`). Возвращает `HasMetadata=false`, если ничего не извлекалось |
| `GetUserStorageInfo` / `GetUserStorageInfoServer` | Информация о квоте |
| `UploadAvatarServer` | Загрузка аватара пользователя (служебный) |

### Облачная иерархия + галерея (вложенно в `Features/Cloud/`)

`CreateDirectory`, `RenameDirectory`, `MoveDirectory`, `DeleteDirectory`, `ListDirectory`, `ListDirectoryDetailed`, `AttachFile`, `RenameFileEntry`, `MoveFileEntry`, `DeleteFileEntry`, `GetPath`, `ListUserImages` (deprecated), `ListUserMedia` (фото/видео по `MediaKind`), `SetVideoThumbnail`. **Корзина**: `ListTrash`, `RestoreFromTrash`, `DeleteFromTrash`, `EmptyTrash` (`DeleteFileEntry`/`DeleteDirectory` теперь soft-delete). **Избранное**: `AddFavorite`/`RemoveFavorite` (по `file_id`, идемпотентны), `ListFavorites` (cursor-пагинация, исключает корзину и осиротевшие ссылки). **Публичные ссылки**: `CreateShare` (проверка владения, токен base64url), `ListMyShares` (cursor-пагинация), `RevokeShare` (идемпотентно). `CopyFileEntry` **удалён** (инвариант одной директории). Подробнее — [[modules/backend-files-cloud]].

`ResolveShare` (публичный резолв токена) — в служебном `FilesServerApi` (`Features/Cloud/ResolveShare/`, без `UserContext`): по токену отдаёт `download_url` через `FileUrlHelper` и инкрементит `ClickCount`. Зовётся из Web-роута `/s/{token}`.

### Альбомы (вложенно в `Features/Album/`)

7 фич: `CreateAlbum`, `UpdateAlbum`, `DeleteAlbum`, `AddItemsToAlbum`, `RemoveItemsFromAlbum`, `ListAlbums`, `ListAlbumItems`. См. [[api/files-api]] · `AlbumApi`.

> **Не реализовано**: стикеры, бейджи, постеры — нет ни в `Features/`, ни в `Shared/BarkCloud.Proto/files_api.proto`.

## Зависимости

- Использует: `BarkCloud.Proto`, `BarkCloud.GrpcServer`, `BarkCloud.Shared.*`, EF Core, PostgreSQL, MinIO (S3 SDK), RabbitMQ, MediatR, **SixLabors.ImageSharp** (превью изображений), **FFMpegCore** (превью видео), **MetadataExtractor** (EXIF фото), **UglyToad.PdfPig** (метаданные PDF), **DocumentFormat.OpenXml** (метаданные DOCX/XLSX/PPTX)
- Образ Files содержит бинарь `ffmpeg`/`ffprobe` (COPY из `mwader/static-ffmpeg` в `Dockerfile`/`Dockerfile.slim`)
- Тесно связан с MinIO (см. [[structure/infrastructure]])
