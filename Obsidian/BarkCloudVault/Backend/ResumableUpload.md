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
| `Backend/BarkCloud.Files/Services/UploadSessionMaintenance.cs` | `UploadSessionMaintenance` | Истечение и очистка сессий |
| `Backend/BarkCloud.Files/Services/UploadSessionProcessor.cs` | `UploadSessionProcessor` | Обработка завершённого оригинала |
| `Backend/BarkCloud.Files/Consumers/ProcessUploadedFileConsumer.cs` | `ProcessUploadedFileConsumer` | Фоновая очередь обработки |
| `Backend/BarkCloud.Files/Scheduling/UploadProcessingQueue.cs` | `ConfigureUploadProcessing` | Очередь и scheduled redelivery; детали в [[Backend/Files#scheduler обработки загрузок]] |
| `Backend/BarkCloud.Files/Scheduling/ScheduledMessageRecoveryListener.cs` | `ScheduledMessageRecoveryListener.JobWasExecuted` | Повторная отправка после ошибки scheduler |
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
- Сервис резервирует объявленный размер в квоте при создании сессии. При готовности размер
  резерва освобождается и учитывается фактический оригинал; отмена, истечение и ошибка
  обработки освобождают резерв при успешно записанном terminal-состоянии.
- Legacy `FilesApi.GetUploadUrl` и `POST /upload/{uploadId}` остаются отдельным маршрутом multipart-загрузки. URL ведёт в Files и не является ссылкой S3.

## Зависимости и взаимодействия

`UploadSessionCoordinator` использует `IMultipartUploadStore`, `StorageQuotaService`, `S3BucketRegistry` и состояние из PostgreSQL. Завершение сессии публикует `ProcessUploadedFile` через MassTransit; `UploadSessionProcessor` вызывает файловый pipeline, после чего обновляет статус и готовность `UploadFile`.

## Повторы и конечные исходы обработки

Контракт `ProcessUploadedFile(Guid SessionId)` находится в
`Shared/BarkCloud.Shared.Queue/Files/ProcessUploadedFile.cs`.
`UploadSessionCoordinator.MoveToProcessingAsync` публикует сообщение через EF bus outbox
вместе с переходом в `Processing`. Pipeline S3/ffmpeg выполняется вне EF-транзакции;
короткая транзакция фиксирует `Ready` после успешной обработки.

`UploadProcessingQueue.ConfigureUploadProcessing` задаёт
`ConcurrentMessageLimit = 2` на каждый Files process и четыре scheduled redelivery через
Quartz с интервалами 10 секунд, 1, 5 и 15 минут. Локальных in-memory retry нет. Scheduler
хранит доставки в PostgreSQL `FilesDb.files_quartz`; ожидание redelivery освобождает
consumer-слоты. `ScheduledMessageRecoveryListener` повторяет транспортную отправку через
30 секунд с копированием payload и headers; это не дополнительная processing delivery.
Границы Quartz recovery и остановка автоматического продвижения при ошибке PostgreSQL —
[[Backend/Files#границы восстановления scheduler]].

Если пятая processing-попытка и terminal-состояние `Failed` успешно записаны, стабильная
ошибка pipeline приводит к `processing_retries_exhausted`, освобождению резерва и cleanup.
Успешно записанная integrity error даёт `Failed/integrity_mismatch`.
`UploadProcessingOutcome.Failed` завершает `Consume` без redelivery. Доставка уже
`Ready`, `Failed`, `Cancelled` или `Expired` сессии даёт `NoOp`. Эти исходы зависят
от сохранения состояния и не являются общей гарантией при ошибках БД.

`ProcessingAttempts` и число scheduled deliveries независимы. Processor увеличивает
`ProcessingAttempts` перед pipeline без отдельного `SaveChanges` checkpoint; операции
pipeline могут сохранить общий `FilesContext`. `StorageMigrationPausedException`
уменьшает `ProcessingAttempts` и повторно выбрасывается, но доставка всё равно расходуется.
Если после инкремента на пятой broker delivery значение остаётся меньше пяти, Failed-ветвь
processor не срабатывает. Ошибка сохранения в generic catch, `FailAsync` или финальном
`Ready` также может вывести обработку за ожидаемый lifecycle. При таких условиях сессия
может остаться в `Processing` с резервом после доставки сообщения в
`process-uploaded-file_error` и публикации `Fault`; не любое отставание счётчика или
любая ошибка ведёт к этому исходу.

`UploadSessionMaintenance` обрабатывает истёкшие multipart-сессии в `Uploading`. Путь для
`Processing` ограничен legacy-сессиями с пустым `MultipartUploadId`; он не сверяет
зависшие resumable-сессии после ошибки consumer. Fault consumer не зарегистрирован в
`Program.cs`, поэтому автоматическая reconciliation таких сессий не выполняется.

`ConcurrencyToken` в `UploadSession` и конфигурации `FilesContext` защищает конкурентную
запись EF, но не сериализует внешние pipeline side effects до сохранения. Две параллельные
доставки одной `SessionId` отдельно не изолированы; это не доказывает, что side effects
обязательно повторятся или что уже существует pipeline claim.

`UploadTestHost` проверяет graceful shutdown с собственными MassTransit timeout.
Production `Program.cs` их явно не задаёт. Hard-kill и crash windows между pipeline,
`Ready`, ACK и Quartz job не покрыты; ограничения стенда перечислены в
[[Platform/Testing]].
