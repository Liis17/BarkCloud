# Files — возобновляемая загрузка

Parent: [[Backend/Files]]
See also: [[Web/WebApp]] · [[Shared/SharedLibraries]] · [[Api/FilesClientGuide]]

## Назначение

Resumable-сессия загружает файл частями в S3 multipart. gRPC `FilesApi` управляет сессией, а байты передаются HTTP-запросами прямо в Files. Обработка оригинала и производных объектов завершается отдельно от отправки последней части.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Files/Domain/UploadSession.cs` | `UploadSession` | Состояние сессии и резерв квоты |
| `Backend/BarkCloud.Files/Services/UploadSessionCoordinator.cs` | `UploadSessionCoordinator` | Сессия, части, resume и complete |
| `Backend/BarkCloud.Files/Infrastructure/S3MultipartUploadStore.cs` | `S3MultipartUploadStore` | S3 multipart операции |
| `Backend/BarkCloud.Files/Services/UploadSessionProcessor.cs` | `UploadSessionProcessor` | Обработка завершённого оригинала |
| `Backend/BarkCloud.Files/Consumers/ProcessUploadedFileConsumer.cs` | `ProcessUploadedFileConsumer` | Фоновая очередь обработки |
| `Backend/BarkCloud.Files/Host/FilesController.cs` | `UploadSessionPart` | HTTP endpoint для передачи части |
| `Backend/BarkCloud.Web/Endpoints/CloudApiEndpoints.cs` | upload endpoints | Web control plane |
| `Shared/BarkCloud.Proto/files_api.proto` | `FilesApi` | gRPC-контракт |

## Публичные контракты

`FilesApi`: `CreateUploadSession`, `GetUploadSession`, `ResumeUploadSession`, `CompleteUploadSession`, `CancelUploadSession`.

Web предоставляет соответствующие маршруты: `POST /api/files/uploads`, `GET /api/files/uploads/{id}`, `POST /api/files/uploads/{id}/resume`, `POST /api/files/uploads/{id}/complete`, `DELETE /api/files/uploads/{id}`.

Байты идут в Files через `PUT /file-upload/{sessionId}/parts/{partNumber}`. Запрос содержит `Content-Type: application/octet-stream`, точный `Content-Range: bytes start-end/total`, `Content-Length` и `X-Upload-Token`. Ответ подтверждает `partNumber` и `size`.

## Ограничения и важные детали

- Создание принимает `idempotency_key`, `file_name`, `file_size`, `content_type`, `sha256`. Ключ уникален в пределах владельца: повтор того же описания возвращает его сессию, другое описание приводит к `UploadIdempotencyConflictException`.
- Состояния: `uploading → processing → ready | failed`; незавершённая сессия также может стать `cancelled` или `expired`. Неактивная загрузка истекает через 24 часа.
- Размер части не меньше 16 MiB; он увеличивается, если нужно уложить объект максимум в 10 000 частей. Максимальный размер объекта — 5 TiB. Последняя часть может быть короче остальных.
- Сервер проверяет номер части, смещение, общий размер и длину тела по объявленному размеру файла. Повторный номер части заменяет соответствующую часть multipart.
- `S3MultipartUploadStore.UploadPartAsync` полностью буферизует неперематываемое тело одной части для обычного S3: AWS SDK требует seekable-поток при включённой подписи тела. До 64 KiB буфер хранится в памяти, далее — во временном файле в `Path.GetTempPath()`, удаляемом при успехе, ошибке или отмене. Объём буфера ограничен объявленным размером части; короткое тело отклоняется до обращения в S3. На временном диске требуется место для одновременно принимаемых частей. R2 с отключённой подписью тела продолжает получать поток напрямую.
- Токен содержит 32 случайных байта; в БД хранится SHA-256 токена. `ResumeUploadSession` выпускает новый токен, старый становится недействительным. `uploaded_parts` строится по подтверждённым данным S3 и сохранённым квитанциям Files; часть без ETag нужно отправить повторно.
- `CompleteUploadSession` проверяет части и переводит сессию в `processing`; consumer запускает общий файловый pipeline. Клиенту следует ждать `ready` перед `CloudApi.AttachFile`. Готовность файла требует `UploadedAt` и непустого `Etag`.
- Сервис резервирует объявленный размер в квоте при создании сессии. При готовности размер резерва освобождается и учитывается фактический оригинал; отмена, истечение и ошибка обработки освобождают резерв.
- Legacy `FilesApi.GetUploadUrl` и `POST /upload/{uploadId}` остаются отдельным маршрутом multipart-загрузки. URL ведёт в Files и не является ссылкой S3.

## Зависимости и взаимодействия

`UploadSessionCoordinator` использует `IMultipartUploadStore`, `StorageQuotaService`, `S3BucketRegistry` и состояние из PostgreSQL. Завершение сессии публикует `ProcessUploadedFile` через MassTransit; `UploadSessionProcessor` вызывает файловый pipeline, после чего обновляет статус и готовность `UploadFile`.

## Повторы и конечные исходы обработки

Контракт `ProcessUploadedFile(Guid SessionId)` остаётся в
`Shared/BarkCloud.Shared.Queue/Files/ProcessUploadedFile.cs`. Complete публикует его
через EF bus outbox вместе с переходом сессии в `Processing`. Обработка S3/ffmpeg не
удерживает EF-транзакцию; короткая транзакция фиксирует готовность после pipeline.

Общий endpoint `UploadProcessingQueue.ConfigureUploadProcessing` сохраняет
`ConcurrentMessageLimit = 2`. Локальных in-memory retry нет. Четыре scheduled redelivery
через Quartz выполняются с интервалами **10 секунд, 1, 5 и 15 минут**: первая обработка
и четыре повторные доставки дают максимум пять попыток. Ожидание хранится в PostgreSQL
`FilesDb.files_quartz`, consumer-слоты освобождаются. Расписание и номер redelivery
сохраняются при рестартах Files/RabbitMQ с сохранёнными данными. Отдельные транспортные
повторы отправки через 30 секунд не меняют номер redelivery или `ProcessingAttempts`.

`UploadSessionProcessor` завершает integrity failure сразу как `Failed` с
`integrity_mismatch`. Постоянная ошибка pipeline на пятой processing-попытке даёт
`Failed`, `processing_retries_exhausted`, освобождение резерва и cleanup. Возвращённый
`UploadProcessingOutcome.Failed` успешно завершает Consume и не вызывает redelivery.
Повторная доставка готовой сессии даёт `Ready → NoOp`, без повторного pipeline и записи
успеха. Cancellation по токену consumer пробрасывается без фиксации незавершённой
processing-попытки; пауза миграции хранилища не увеличивает `ProcessingAttempts`.

Необработанное исключение после четырёх redelivery уходит в стандартную очередь
`process-uploaded-file_error` и публикует `Fault<ProcessUploadedFile>`. Дополнительного
Fault-consumer нет; сессия может оставаться `Processing` до ручного разбирательства.
Интерфейсы эксплуатации дополнены таблицами Quartz и
`files-upload-scheduler_error`. См. [[Platform/Infrastructure]] и [[Platform/Testing]].
