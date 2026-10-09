# Backend — Files

Parent: [[Index]]
See also: [[Backend/FilesCloud]] · [[Backend/ResumableUpload]] · [[Backend/OriginalLifetime]] · [[Backend/ImagePlaceholders]] · [[Backend/DynamicFolders]] · [[Web/S3Migration]]

## Назначение

`BarkCloud.Files` принимает файлы, хранит оригиналы и производные объекты в S3-совместимом хранилище, ведёт метаданные в PostgreSQL и выдаёт клиентские и межсервисные API. Файловый блоб и его запись в облачной иерархии — разные сущности; модель и операции иерархии описаны в [[Backend/FilesCloud]].

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Files/Program.cs` | регистрации и хост | gRPC, HTTP, БД, очередь и фоновые службы |
| `Backend/BarkCloud.Files/Host/FilesApiService.cs` | `FilesApiService` | клиентские RPC |
| `Backend/BarkCloud.Files/Host/FilesController.cs` | `FilesController` | HTTP-передача частей, legacy upload и download |
| `Backend/BarkCloud.Files/Features/UploadFile/UploadFileCommandHandler.cs` | обработка загрузки | оригинал, хеш, метаданные и превью |
| `Backend/BarkCloud.Files/Domain/UploadFile.cs` | `UploadFile` | запись блоба и профиль хранения |
| `Backend/BarkCloud.Files/Persistence/FilesContext.cs` | EF-модель | таблицы и ограничения |
| `Backend/BarkCloud.Files/Services/PreviewPersistenceService.cs` | сохранение превью | отдельные блобы и связи превью |
| `Backend/BarkCloud.Files/Infrastructure/S3Uploader.cs` | `S3Uploader` | чтение, запись и удаление объектов |
| `Shared/BarkCloud.Proto/files_api.proto` | Files-сервисы | публичные контракты |

## Публичные контракты

| API | Назначение |
|---|---|
| `FilesApi` | Загрузка и resumable-сессии, временные download URL, проверка SHA-256, сведения о хранилище и метаданные |
| `CloudApi` | Папки, записи, галерея, корзина и обмен файлами — [[Backend/FilesCloud]] |
| `DynamicFolderApi` | Умные папки — [[Backend/DynamicFolders]] |
| `AlbumApi` | `CreateAlbum`, `UpdateAlbum`, `DeleteAlbum`, `AddItemsToAlbum`, `RemoveItemsFromAlbum`, `ListAlbums`, `ListAlbumItems`; фото- и видеоальбомы, один блоб может входить в несколько альбомов |
| `MusicApi` | Треки и URL проигрывания, CRUD плейлистов, состав/порядок треков, публичные ссылки и пользовательские гранты |
| `SearchApi` | `Search`, `ResolveHit`, `GetFileSearchMetadata`, `ReplaceFileSearchMetadata`; разделённые секции поиска |
| `FilesServerApi` | Межсервисные данные файла, загрузка аватара, `ResolveShare`/`ResolveFolderShare`/`ResolveAlbumShare`/`ResolveMusicPlaylistShare` и барьер миграции S3 |

Для клиентского сценария сессий см. [[Backend/ResumableUpload]] и [[Api/FilesClientGuide]]. Контракты объявлены в `Shared/BarkCloud.Proto/files_api.proto`.

`GetTempDownloadUrl` возвращает временные ссылки по `file_id`. `CheckFileHash`/`CheckFileHashes` проверяют наличие SHA-256; `GetFileMetadata` возвращает извлечённые EXIF, видео, аудио и документные поля; `GetUserStorageInfo` включает квоту и активный резерв загрузки.

| HTTP-маршрут | Поведение |
|---|---|
| `PUT /file-upload/{sessionId}/parts/{partNumber}` | Часть resumable-загрузки; запрос несёт `Content-Range` и `X-Upload-Token` |
| `POST /upload/{uploadId}` | Legacy multipart upload; вызывается по URL из `FilesApi.GetUploadUrl` |
| `GET /download/{fileId}` | Выдача файла; поддерживается один явный byte-range |

`GetUploadUrl` создаёт запись `UploadFile` и возвращает URL собственного HTTP-маршрута Files. Это не presigned URL S3.

## Зависимости и взаимодействия

- EF Core и PostgreSQL хранят записи файлов, хеши, метаданные, загрузочные сессии и производные объекты.
- `S3BucketRegistry` выбирает профиль для каждого объекта; `UploadFile.StorageProfileId` нужен для последующего чтения и удаления.
- После загрузки `ProcessUploadedFileConsumer` запускает извлечение метаданных и создание производных объектов через MassTransit/RabbitMQ. `Scheduling/UploadProcessingQueue.cs` задаёт два слота `process-uploaded-file` и scheduled redelivery через persistent Quartz/PostgreSQL; ожидание повторов освобождает слоты. Подробности исходов и расписания — [[Backend/ResumableUpload]].
- Изображения обрабатываются ImageSharp; видео анализируются и обрабатываются через FFMpegCore/ffmpeg.
- Конфигурация профилей S3 поступает из Configuration; Web использует Files API и HTTP upload-маршрут.

## Ограничения и важные детали

- `UploadFileReadiness` считает блоб готовым при заданном `UploadedAt` и непустом `Etag`. Клиентские выборки используют это условие, чтобы не показывать неполностью обработанные файлы.
- `UploadFileType`: `Unknown=0`, `UserAvatar=1`, `CloudFile=2`. `MediaKind`: `Other=0`, `Photo=1`, `Video=2`, `Document=3`, `Audio=4`; категория вычисляется через `FileExtensions.GetMediaKind()` по имени файла.
- Каждая загрузка оригинала создаёт отдельный блоб. SHA-256 сохраняется для `CheckFileHash`/`CheckFileHashes`; он не объединяет одинаковые оригиналы. Превью могут дедуплицироваться по SHA-256.
- Для облачных изображений pipeline создаёт отдельный JPEG-вариант просмотра (`JpegView`) и связывает его как превью с `TargetWidth=0`. Оригинал сохраняется отдельно; обычные превью хранятся как самостоятельные `UploadFile`.
- `UploadFileInfo` передаёт обычные превью в `previews`; `preview_url` помечено устаревшим. Поля `jpeg_view_file_id`/`jpeg_view_url` описывают отдельный JPEG-вариант. Цвета превью — [[Backend/ImagePlaceholders]].
- Срок жизни записей и оригиналов, включая корзину и окончательное удаление, описан в [[Backend/OriginalLifetime]].

## Scheduler обработки загрузок

`Backend/BarkCloud.Files/Scheduling/UploadProcessingQueue.cs` регистрирует Quartz 3.15.0
и MassTransit.Quartz 8.5.2. Очередь `files-upload-scheduler` durable, без auto-delete;
плагин RabbitMQ и отдельный scheduler-контейнер не нужны. `AddQuartzConsumers` управляет
запуском после готовности bus и остановкой Quartz. Миграции `FilesContext` выполняются
в `Program.cs` до запуска hosted services.

Хранилище — существующий `FilesDb`, схема `files_quartz`, префикс `files_quartz.qrtz_`,
стабильное имя `BarkCloud.Files.UploadScheduler`, instance ID `AUTO`, clustering,
System.Text.Json serializer. Миграция
`Persistence/Migrations/20261009000000_AddUploadScheduler.cs` создаёт таблицы по Quartz
3.15.0 без очистки; `Down` сохраняет таблицы и pending-доставки, повторное применение
также сохраняет данные.

`ScheduledMessageRecoveryListener` после окончательной ошибки штатного
`ScheduledMessageJob` сохраняет новый trigger через 30 секунд. Копирует весь
`MergedJobDataMap`: payload, destination, headers (включая номер redelivery), transport
properties и идентификаторы. Транспортные повторы не вызывают processor и не увеличивают
`ProcessingAttempts`. Misfire исполняется сразу после восстановления scheduler.
Ожидание reconnect RabbitMQ может удерживать job scheduler, но не consumer-слоты.

Эксплуатация и откат — [[Platform/Infrastructure]], проверки — [[Platform/Testing]].
