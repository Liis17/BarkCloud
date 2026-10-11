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
| `Backend/BarkCloud.Files/Services/SearchRanking.cs` | `SearchTerms`, `SearchQueryExtensions`, `SearchCursor` | SQL-правило ранга, набор кандидатов и курсор поиска |
| `Backend/BarkCloud.Files/Services/UnifiedSearchService.cs` | `UnifiedSearchService` | вход поиска, пагинация секций и построение карточек |
| `Backend/BarkCloud.Files/Services/UnifiedSearchService.Sources.cs` | источники секций | SQL-выборки файлов, альбомов, плейлистов, папок и shared-объектов |
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

### Поиск

`Backend/BarkCloud.Files/Services/SearchRanking.cs` задаёт общее правило PostgreSQL для отбора, ранга и сходства поля: 4 — точное совпадение, 3 — префикс, 2 — подстрока, 1 — `word_similarity(value, query) >= 0.45` для запроса от четырёх символов. Для рангов 2–4 сходство равно 1. Запрос нормализуется через NFKC, обрезку краёв, схлопывание пробелов и invariant lowercase. Алиасы и теги сравниваются по сохранённому `NormalizedValue`; имена и метаданные — в исходном виде средствами PostgreSQL (`lower`, `ILIKE`, `pg_trgm`). Сырые колонки не нормализуются в SQL.

`Backend/BarkCloud.Files/Services/UnifiedSearchService.Sources.cs` собирает отдельного SQL-кандидата для каждого поля, сводит ранг и сходство по максимуму и после ограничения страницы выбирает подпись среди кандидатов с теми же значениями ранга и сходства. `Backend/BarkCloud.Files/Services/UnifiedSearchService.cs` выполняет общий запрос секций и передаёт выбранные подписи в карточки. Приоритет полей: файлы — имя, алиас, тег, аудиозаголовок, исполнитель, альбом, название документа, автор, тема; альбомы и плейлисты — имя, описание. При ничьей побеждает меньшее значение `Order`, затем значение по `COLLATE "C"` (байтовый порядок UTF-8 в PostgreSQL). `match_value` для алиаса и тега сохраняет исходный регистр и пробелы.

Папки, пользовательские умные папки и shared-объекты имеют одно поле `name`. Системные умные папки ранжируются SQL-выражениями `SearchTerms` по массиву системных имён и участвуют в общей сортировке курсора. Формат курсора и ограничение чтения `limit + 1` не меняются; `ResolveHit` без поискового запроса возвращает пустые `match_field` и `match_value`. Подробности клиентского контракта — [[Api/FilesClientGuide#поиск]].

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

`ScheduledMessageRecoveryListener.JobWasExecuted` обрабатывает окончательную ошибку
штатного `ScheduledMessageJob`: сохраняет trigger через 30 секунд и копирует весь
`MergedJobDataMap`, включая payload, destination, headers, transport properties и
идентификаторы. Misfire исполняется сразу после восстановления scheduler. Этот транспортный
повтор не вызывает processor и не увеличивает `ProcessingAttempts`.

В пути `process-uploaded-file` MassTransit `RedeliveryRetryFilter.Send` сначала
ожидает `MessageRedeliveryContext.ScheduleRedelivery`, затем вызывает `NotifyConsumed`.
Quartz `ScheduleMessageRedeliveryContext.ScheduleRedelivery` возвращает `ScheduleSend`.
Фильтр вызывает `NotifyConsumed` только после `await ScheduleRedelivery`; завершение и
ACK исходной delivery поэтому ожидают завершения отправки команды расписания, а обработка
занимает один consumer slot до этого. Publisher confirms MassTransit RabbitMQ
настроены ([[Platform/Infrastructure]]); подтверждение команды брокером не означает, что
Quartz consumer уже сохранил job в PostgreSQL. Источники MassTransit 8.5.2:
[`RedeliveryRetryFilter.cs`](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/MassTransit/Middleware/RedeliveryRetryFilter.cs)
и
[`ScheduleMessageRedeliveryContext.cs`](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/MassTransit/Contexts/Context/ScheduleMessageRedeliveryContext.cs).
После завершения отправки исходная обработка может освободить consumer slot; затем
scheduler consumer сохраняет trigger в `files_quartz`. Ожидание интервалов 10 секунд,
1, 5 или 15 минут происходит в Quartz и не удерживает slot.

Позже `ScheduledMessageJob.Execute` отправляет сообщение в destination. Если RabbitMQ
недоступен, send может ожидать reconnect, пока Quartz job остаётся выполняющимся; этот путь
не занимает consumer slot `process-uploaded-file`. Тестовый комментарий описывает ожидание
reconnect и прерывание send через `IScheduler.Interrupt`
(`Tests/Backend/BarkCloud.Files.IntegrationTests/UploadRedeliveryTests.cs:133–134`).
Это отдельный путь от отправки scheduler command.

Для `process-uploaded-file` настроен scheduled redelivery без локального in-memory retry.
`ConcurrentMessageLimit = 2` действует на каждый экземпляр Files, а не на весь кластер.

Эксплуатация и откат — [[Platform/Infrastructure]], проверки — [[Platform/Testing]].

### Границы восстановления scheduler

MassTransit.Quartz 8.5.2 задаёт endpoint retry `5 × 250 мс` для команд scheduler в
`ScheduleMessageConsumerDefinition`. `ScheduleMessageConsumer.Consume` записывает trigger
через `IScheduler.ScheduleJob`; `EnsureJobExists` создаёт durable
`ScheduledMessageJob` с `RequestRecovery().StoreDurably()`. В
`ScheduledMessageJob.Execute` ограничен immediate refire. Это отдельные механизмы, и ни
один сам по себе не гарантирует crash-safe доставку.

Если запись Quartz job в PostgreSQL постоянно завершается ошибкой, scheduler command может
попасть в `files-upload-scheduler_error`, останавливая автоматическое продвижение этой
доставки. Publisher confirmation для `ScheduleMessage` подтверждает приём команды
брокером, но не запись Quartz job в PostgreSQL. `5 × 250 мс` — сумма задержек retry без
учёта длительности операций PostgreSQL, а не порог outage. Проверка отказа требует сверить
error queue, Quartz triggers, upload queue и состояние сессии после восстановления
PostgreSQL.

Если PostgreSQL недоступен при сохранении recovery trigger в
`ScheduledMessageRecoveryListener.JobWasExecuted`, фактическое поведение Quartz store и
восстановления не установлено: unit-тест listener использует mock `IScheduler`. Источники:
[`ScheduleMessageConsumerDefinition.cs`](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/Scheduling/MassTransit.QuartzIntegration/Configuration/Configuration/ScheduleMessageConsumerDefinition.cs),
[`ScheduleMessageConsumer.cs`](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/Scheduling/MassTransit.QuartzIntegration/QuartzIntegration/ScheduleMessageConsumer.cs)
и
[`ScheduledMessageJob.cs`](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/Scheduling/MassTransit.QuartzIntegration/QuartzIntegration/ScheduledMessageJob.cs).
