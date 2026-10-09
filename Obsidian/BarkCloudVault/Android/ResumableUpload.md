# Android — возобновляемая загрузка

Parent: [[Android/AndroidApp]] · Серверный контракт: [[Backend/ResumableUpload]] · Файловые операции: [[Backend/Files]]

## Назначение

Единая постоянная очередь для исходящих Android-загрузок: ручных, из галереи и альбома, системного share target и автозагрузки. Большие файловые данные передаются частями; состояние каждой задачи хранится локально и связывает очередь с серверной upload-сессией.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/upload/UploadWorker.kt` | `UploadWorker` | Обработка, восстановление, завершение и привязка |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/upload/UploadQueueStore.kt` | `UploadQueueStore` | Постановка в очередь, staging, retry и отмена |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/upload/UploadModels.kt` | `UploadJob`, `UploadPhase` | Модель задачи и состояние процесса |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/upload/UploadScheduler.kt` | `UploadScheduler` | Уникальная WorkManager-задача очереди |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/net/FileTransferService.kt` | upload-session и part методы | gRPC control plane и HTTP передача частей |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/upload/UploadPartMath.kt`, `UploadFileHasher.kt` | `UploadPartMath`, `UploadFileHasher` | Хеш и расчёт диапазонов частей |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/persistence/BarkCloudDatabase.kt`, `UploadDao.kt` | `BarkCloudDatabase`, `UploadDao` | Сохранение очереди в Room |

## Публичные контракты

- Любой исходный `content://` URI сначала копируется в приватную папку приложения `files/upload_queue`. Повторное чтение staging-файла необходимо и для хеширования, и для отправки диапазонов.
- Control plane использует gRPC Files API; data plane отправляет части через HTTP по базе `fileUploadBase` текущей серверной конфигурации. Точный формат серверного API описан в [[Backend/ResumableUpload]].
- `UploadJob` хранит источник и назначение, staged path, idempotency key, session ID, размер части, прогресс и целевой каталог/альбом.
- `UploadDestination.SYSTEM_BY_MEDIA_KIND` передаёт файлы без выбранного каталога на авто-распределение; `DIRECTORY` сохраняет выбранный `directoryId`. Для загрузки в альбом задача хранит `albumId`.
- Загрузка аватара остаётся отдельным legacy multipart-путём через `UserRepository`; он не проходит через файловую очередь.

## Зависимости и взаимодействия

1. При постановке в очередь `UploadQueueStore` сохраняет staging-файл и Room-запись с новым idempotency key.
2. `UploadWorker` считает SHA-256, создаёт или восстанавливает серверную сессию и отправляет только неподтверждённые части. Размер части назначает сервер; тело каждой части читает соответствующее окно staging-файла через `RandomAccessFile`, не загружая файл целиком в память.
3. Подтверждённая сервером часть учитывается, только если у неё есть ETag и размер совпадает с ожидаемым. Части одного файла отправляются последовательно; параллельно обрабатываются максимум четыре файла.
4. После завершения передачи клиент согласует состояние сессии, ожидает обработки сервером и привязывает готовый file ID к каталогу или системному разделу. Если указан альбом, после привязки он добавляется отдельно.
5. `UploadQueueStore` удаляет staging после завершения. Ошибка после готовности файла сохраняет фазу `UPLOADED_NOT_ATTACHED`; повтор задачи выполняет только привязку.
6. WorkManager запускает общий foreground data-sync worker при доступной сети. Автозагрузка медиатеки ставит backup-задачи в ту же очередь.

## Ограничения и важные детали

- Состояния Room отражают фазы `QUEUED → HASHING → CREATING_SESSION → UPLOADING → COMPLETING → PROCESSING → ATTACHING → COMPLETED`; ошибки, отмена и `PAUSED` для backup — отдельные состояния.
- Для восстановления используются сохранённые idempotency key и session ID, затем серверный список принятых частей. Локальный `bytesSent` нужен для отображения прогресса; upload-token хранится только в памяти worker и заново выдаётся при resume.
- Если ответ на завершение потерян, worker сверяет серверное состояние перед повтором; если готовый файл не привязался, повтор не передаёт байты заново.
- Отмена задачи помечает её в Room и удаляет staging; worker отменяет связанную серверную сессию при следующем наблюдении задачи. Пауза доступна backup-задачам и проверяется между частями.
- `UploadScheduler` использует уникальную цепочку WorkManager с политикой `KEEP`, поэтому повторные enqueue не создают параллельный worker для очереди.
