# Upload 2.0 — возобновляемая Web-загрузка

Parent: [[modules/backend-files]] · Web: [[modules/backend-web]] · API: [[api/files-api]] · Queue: [[modules/shared-queue]] · Infrastructure: [[structure/infrastructure]]

## Назначение

Upload 2.0 — основной транспорт загрузки файлов из Web. Control plane остаётся cookie-auth API Web и gRPC `FilesApi`, а data plane идёт напрямую `Browser → nginx/Vite → Files HTTP/1 → S3/MinIO`. Web больше не принимает и не пересылает байты V2-файлов.

V2 включён для массовой Web-загрузки, обложек музыкальных плейлистов и ручных превью видео; Web-релиз поднят до `v1.4.0`. Legacy `GetUploadUrl`, `/upload/{id}`, `/web/upload/{id}` и `/api/files/upload` сохранены для мобильных/desktop-клиентов и ручного rollback.

## Состояния и готовность

Серверный источник истины — `UploadSession`:

```text
uploading → processing → ready
                     ↘ failed
uploading → cancelled | expired
```

- `ready` требует одновременно заполненные `UploadFile.UploadedAt` и `UploadFile.Etag`; EF-запросы используют общий translatable `UploadFileReadiness.WhereReady()`.
- До `ready` placeholder не возвращается из list/search/music/metadata/download/share/activity путей и не может быть передан в `AttachFile`, delete-media, избранное, обложку альбома или ручную замену превью.
- `Cancel` допустим только для `uploading`; `failed`, `cancelled` и `expired` запускают идемпотентную очистку.
- Терминальные сессии хранятся 7 дней; `uploading` истекает через 24 часа после последней успешно принятой части.

## Control plane

Cookie-auth Web endpoints:

| HTTP | Назначение |
|---|---|
| `POST /api/files/uploads` | Создать идемпотентную сессию и зарезервировать квоту |
| `GET /api/files/uploads/{id}` | Получить состояние без upload-token |
| `POST /api/files/uploads/{id}/resume` | Выпустить новый token и вернуть фактические части из S3 |
| `POST /api/files/uploads/{id}/complete` | Проверить/завершить multipart и поставить processing-задачу |
| `DELETE /api/files/uploads/{id}` | Отменить незавершённую сессию |

Web проксирует эти операции в аддитивные RPC `FilesApi`: `CreateUploadSession`, `GetUploadSession`, `ResumeUploadSession`, `CompleteUploadSession`, `CancelUploadSession`. Ключ идемпотентности уникален по `(OwnerId, IdempotencyKey)`: тот же дескриптор возвращает сессию, другой — `UploadIdempotencyConflictException`.

## Data plane и multipart

- Endpoint Files: `PUT /file-upload/{sessionId}/parts/{partNumber}` с `application/octet-stream`, точным `Content-Range` и `X-Upload-Token`.
- Token — 32 случайных байта (256 бит); в `UploadSessions` хранится SHA-256 token. Новый token инвалидирует предыдущий. Token и полный SHA файла не логируются и не попадают в IndexedDB.
- Базовый размер части — 16 MiB; для очень больших файлов он безопасно, без `Int64` overflow, округляется вверх так, чтобы multipart содержал не более 10 000 частей. Максимальный заявленный объект — 5 TiB, последняя часть может быть меньше.
- Части одного файла отправляются последовательно. Повтор номера части безопасно заменяет её в S3.
- `Resume` использует S3 `ListParts`, а не локальный прогресс браузера. Files дополнительно хранит последний подтверждённый ETag каждой части в `UploadSessionParts`: если gateway вернул пустой `ListParts` или часть без ETag сразу после успешного `UploadPart`, сервер восстанавливает только подтверждённые Files-ответом части. `Complete` самостоятельно проверяет номера/размеры/ETag всех частей и общий размер.
- Если ответ S3 Complete неоднозначен, `ListParts` уже пуст после завершения или Files перезапустился после него, состояние восстанавливается через `HeadObject`; неполный список безопасно логируется без токена/SHA. Повторный и конкурентный `Complete` идемпотентно перечитывает состояние после optimistic-concurrency конфликта.
- `Backend/nginx/cloud.barkfluff.conf` и шаблон `Tools/BarkCloud.Builder/BackendComposeGenerator.cs` направляют `/file-upload/` с 443 прямо в `cloud-files:7026`, `proxy_request_buffering off`; Vite dev proxy направляет тот же path на `localhost:7026`. XHR принимает успешной только JSON-квитанцию Files с ожидаемыми `partNumber/size`: SPA/login HTML от ошибочно настроенного proxy больше не маскируется под загруженную часть.

S3 seam: `IMultipartUploadStore` / `S3MultipartUploadStore` (`InitiateAsync`, `UploadPartAsync`, `ListPartsAsync`, `CompleteAsync`, `AbortAsync`, `HeadAsync`).

## Квота

`StorageQuotaService` в транзакции берёт PostgreSQL advisory lock по owner и считает:

```text
used = ready UploadFile bytes
reserved = ReservedBytes активных UploadSession
```

- `StorageLimitGb = 0` означает unlimited.
- Создание сессии резервирует заявленный размер оригинала до выдачи token.
- `ready` превращает резерв в фактический размер оригинала; cancel/expire/fail освобождают резерв.
- Производные файлы не резервируются и тарифицируются постфактум. Допустим overshoot после enrichment, но следующая сессия будет отклонена.
- `GetUserStorageInfoResponse.reserved_storage` показывает резерв. `UploadQuotaExceededException` несёт `limit/used/reserved/requested`.
- Legacy HTTP upload использует тот же `LegacyUploadQuotaGuard` до синхронной обработки. Последней durable-операцией общего handler после полного enrichment становится marker `LegacyProcessingCompletedAt` (он пишется независимо от disconnect), затем `UploadedAt` и освобождение резерва меняются одной EF-транзакцией. Параллельный запрос к активной сессии без marker отклоняется; replay/maintenance финализирует скрытый оригинал только при наличии marker и совпадении размера. Более ранний crash даёт `failed + CleanupPending`, чтобы недообработанный blob не стал видимым.

## Фоновая обработка

`CompleteUploadSession` в одной EF-транзакции переводит сессию в `processing` и публикует `ProcessUploadedFile { SessionId }` через MassTransit Bus Outbox. Миграция `20260911045624_AddUploadSessionsAndOutbox` добавляет `UploadSessions` и EF outbox/inbox tables.

Очередь `process-uploaded-file` имеет concurrency 2 и интервалы retry: 10 секунд, 1, 5 и 15 минут (до 5 попыток с первой доставкой). Consumer идемпотентен по состоянию сессии; долгий enrichment не оборачивается в consumer EF-outbox транзакцию.

`UploadSessionProcessor` скачивает оригинал один раз во временный disk-файл (`Uploads:TempDirectory` либо системный temp), потоково сверяет размер и SHA-256 и переиспользует существующий enrichment pipeline:

- размеры и EXIF/PDF/OpenXML/ffprobe metadata;
- preview 1024/512/128, audio artwork 512/128;
- HEIC/HEIF JPEG view и video thumbnail;
- ImageSharp identify до decode и предел 200 MP;
- ffprobe timeout 2 минуты, ffmpeg 15 минут с завершением дочернего процесса при отмене.

Size/SHA mismatch и неразбираемый заявленный image/video/audio/PDF/OpenXML формат дают постоянный `failed`. Ошибка необязательных metadata/отдельного preview записывается как warning, и файл становится `ready`. Инфраструктурные ошибки ретраятся; после исчерпания попыток оригинал, placeholder и созданные артефакты удаляются. Переход в `failed` сохраняется до cleanup: ошибка удаления не меняет outcome на retry, а оставляет `CleanupPending` для следующего фонового прохода.

Consumer пишет раздельные outcome-метрики `ready/failed/noop`, retry и длительность обработки; повторная доставка для терминальной сессии считается `noop`, а outcome-log коррелируется по session/file/user. Наружу сохраняется безопасное сообщение без внутренних S3/ffmpeg путей. Повтор привязки после `uploaded_not_attached` передаёт `upload_session_id`, структурированно логируется по session/file/user и увеличивает `upload_attach_retries_total`.

`UploadSessionCleanupService` запускает обслуживание при старте и затем каждые 15 минут: expire/abort, восстановление зависшего legacy reserve, повтор cleanup и удаление старых терминальных записей.

## Web-клиент и восстановление

- `sha256.worker.ts` + `hash-wasm` читают `File` блоками по 4 MiB, публикуют progress и поддерживают AbortSignal; whole-file `file.arrayBuffer()` не используется.
- `uploadQueueStore.ts` хранит в IndexedDB версионированные metadata задачи: task/idempotency/batch ids, дескриптор файла, SHA, session/file ids, attach options, status/progress/error/timestamps. `File`, token и `AbortController` не сохраняются.
- UI-состояния: `hashing`, `checking`, `needs_file`, `uploading`, `processing`, `attaching`, `uploaded_not_attached`, `done`, `failed`, `skipped`.
- После reload `processing` polling продолжается раз в 2 секунды. Для `uploading` требуется повторно выбрать файл: размер проверяется сразу, SHA — окончательно; затем загружаются только отсутствующие S3 parts.
- Глобально одновременно активны максимум четыре файла, части каждого файла последовательны. На `processing`/`attaching` transfer progress остаётся 100%.
- Сетевой retry не уничтожает сессию. Если Complete отвечает `upload_parts_incomplete`, клиент делает `resume`, не считает часть завершённой без подтверждённого ETag и отправляет только отсутствующие части, затем повторяет Complete; новая сессия создаётся только после серверного `failed`/`expired`; обычный Retry делает resume.
- `AttachFile` вызывается только после `ready`. Ошибка даёт `uploaded_not_attached`; Retry повторяет только attach и помечает запрос для метрики. Стабильный `FileAlreadyAttachedException` считается успешным replay.
- Одноразовые загрузки обложек/thumbnail используют V2 и ждут `ready`, но не записываются в постоянную массовую очередь.

## Карта реализации

| Область | Файлы |
|---|---|
| Domain/EF | `Domain/UploadSession.cs`, `Domain/UploadSessionPart.cs`, `Domain/UploadFileReadiness.cs`, `Persistence/FilesContext.cs`, migrations `AddUploadSessionsAndOutbox`, `PersistUploadSessionParts` |
| Multipart/control | `Services/UploadSessionCoordinator.cs`, `Infrastructure/IMultipartUploadStore.cs`, `Infrastructure/S3MultipartUploadStore.cs`, `Host/FilesApiService.cs`, `Host/FilesController.cs` |
| Quota | `Services/StorageQuotaService.cs`, `Services/LegacyUploadQuotaGuard.cs`, `Services/ILegacyUploadCompletionMarker.cs`, `Services/UsersStorageLimitProvider.cs` |
| Processing/cleanup | `Consumers/ProcessUploadedFileConsumer.cs`, `Services/UploadSessionProcessor.cs`, `Services/ExistingUploadEnrichmentPipeline.cs`, `Services/UploadArtifactCleaner.cs`, `Services/UploadSessionMaintenance.cs` |
| Web | `Endpoints/CloudApiEndpoints.cs`, `ClientApp/src/hooks/useUploadManager.tsx`, `ClientApp/src/lib/uploadSessions.ts`, `fileHasher.ts`, `sha256.worker.ts`, `uploadQueueStore.ts` |
| Proxy | `Backend/nginx/cloud.barkfluff.conf`, `Tools/BarkCloud.Builder/BackendComposeGenerator.cs`, `ClientApp/vite.config.ts` |
