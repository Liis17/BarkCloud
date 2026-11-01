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
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/gallery/AutoUploadWorker.kt`, `AutoUploadScheduler.kt` | `AutoUploadWorker`, `AutoUploadScheduler` | Поиск медиатеки, политика сети и запуск backup-очереди |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/gallery/DeviceMediaStore.kt`, `BackupQueueDiscovery.kt` | `DeviceMediaStore`, `BackupQueueDiscovery` | Пакетное чтение MediaStore и сериализация discovery |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/upload/BackupStager.kt`, `UploadFailurePolicy.kt` | `BackupStager`, `UploadFailurePolicy` | Ленивое staging backup-медиа и классификация ошибок |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/net/FileTransferService.kt` | upload-session и part методы | gRPC control plane и HTTP передача частей |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/users/UserRepository.kt` | `setAvatar`, `removeAvatar` | Установка профиля после готовности файла |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/ui/settings/ProfileViewModel.kt` | avatar actions | Foreground-индикатор и защита от параллельных изменений |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/upload/UploadPartMath.kt`, `UploadFileHasher.kt` | `UploadPartMath`, `UploadFileHasher` | Хеш и расчёт диапазонов частей |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/persistence/BarkCloudDatabase.kt`, `UploadDao.kt` | `BarkCloudDatabase`, `UploadDao` | Сохранение очереди в Room |

## Публичные контракты

- Ручные, gallery, album и share задачи по-прежнему сразу копируют исходный `content://` URI в приватную папку приложения `files/upload_queue`. Backup сначала сохраняет только метаданные и ключ `image:<id>`/`video:<id>`; MediaStore URI восстанавливается перед staging.
- Control plane использует gRPC Files API; data plane отправляет части через HTTP по базе `fileUploadBase` текущей серверной конфигурации. Точный формат серверного API описан в [[Backend/ResumableUpload]].
- `UploadJob` хранит источник и назначение, staged path, idempotency key, session ID, размер части, прогресс и целевой каталог/альбом.
- `UploadDestination.SYSTEM_BY_MEDIA_KIND` передаёт файлы без выбранного каталога на авто-распределение; `DIRECTORY` сохраняет выбранный `directoryId`. Для загрузки в альбом задача хранит `albumId`.
- Смена аватара использует `FileTransferService.uploadAvatar` в foreground: URI копируется в уникальный cache-файл, затем отправляется как `USER_AVATAR`. `UserRepository.setAvatar` вызывает `UsersApi.SetProfilePicture` только после состояния `READY`.

## Зависимости и взаимодействия

1. `UploadQueueStore.enqueue` сохраняет staging-файл для ручных, gallery, album и share задач. Автозагрузка читает изображения и видео MediaStore ограниченными пакетами, фильтрует выбранные папки и транзакционно добавляет backup-запись без хеша и файла; параллельные сканы сериализованы, а повторные записи устраняются по media key.
2. `UploadWorker` обходит активную очередь keyset-страницами и обрабатывает не больше четырёх задач одновременно. Для backup перед копированием повторно проверяет включённую автозагрузку, выбранную папку и разрешение. `BackupStager` потоково пишет собственный `.part`, проверяет имя, MIME, размер и дату изменения до/после чтения, считает SHA-256 фактических staged-байтов и атомарно публикует файл. В очереди одновременно удерживается не более четырёх app-private backup-копий; сохранённая копия и серверная сессия переиспользуются при повторе, если фактические байты не изменились. Перед расчётом staging-лимита файлы `FAILED`/`PAUSED` и attach-only задач освобождаются с сохранением хеша и параметров сессии; при инициализации удаляются только неучтённые в Room backup-файлы `.part`/`.stage`.
3. `UploadWorker` создаёт или восстанавливает серверную сессию и отправляет только неподтверждённые части. Размер части назначает сервер; тело каждой части читает соответствующее окно staging-файла через `RandomAccessFile`, не загружая файл целиком в память.
4. Подтверждённая сервером часть учитывается, только если у неё есть ETag и размер совпадает с ожидаемым. Части одного файла отправляются последовательно; параллельно обрабатываются максимум четыре файла.
5. После завершения передачи клиент согласует состояние сессии, ожидает обработки сервером и привязывает готовый file ID к каталогу или системному разделу. Если указан альбом, после привязки он добавляется отдельно. Фаза `ATTACHING`/`UPLOADED_NOT_ATTACHED` обрабатывается до требования staging-файла, поэтому повтор только привязки не копирует исходник.
6. `UploadQueueStore` удаляет staging после завершения. Ошибка после готовности файла сохраняет фазу `UPLOADED_NOT_ATTACHED`; повтор задачи выполняет только привязку. Ошибка одного недоступного источника завершает только его строку; сетевой сбой, quota, ENOSPC или потеря разрешения останавливают захват новых задач и оставляют хвост очереди `QUEUED`.
7. После инициализации Room при активной сессии `BarkCloudApplication` восстанавливает WorkManager-задачу для активной очереди и применяет сохранённую политику автозагрузки. При `OFF` автоматический backup не запускается; ручная очередь остаётся доступной. Фазы `PAUSED` и `FAILED` не считаются активными и не сбрасываются восстановлением. Constraints `AutoUploadWorker` для периодического и одноразового discovery отражают сохранённую политику: `WIFI_ONLY` требует `UNMETERED`, `ANY_NETWORK` — `CONNECTED`, а `OFF` отменяет discovery. При `WIFI_ONLY` `UploadWorker` оставляет backup-записи в активной очереди на metered-сети и не планирует для них повторный запуск; ручные задачи обрабатываются независимо. При смене политики одноразовый запрос заменяется с новыми constraints; при той же политике `UniqueWorkEnqueuer` coalesce-ит `ENQUEUED` запрос и добавляет один successor при `RUNNING`. Повторное обнаружение не создаёт дубликаты backup-задач благодаря media key. `DeviceFoldersScreen` вызывает `apply` при изменении папки без дополнительного повторного `runOnce`.

Отдельный foreground-путь аватара не использует Room или WorkManager. Он запрашивает настоящее имя через `OpenableColumns.DISPLAY_NAME`, MIME — через resolver с fallback `application/octet-stream`, считает SHA-256 и размер staged-файла, отклоняет пустой файл и размер свыше 20 MiB, затем передаёт части последовательно. Состояние после `CompleteUploadSession` опрашивается каждые 2 секунды не дольше 120 секунд; профиль меняется только для `READY` с непустым `file_id`. Cache-файл удаляется после успеха, ошибки или отмены. Аватарный upload не восстанавливается после уничтожения процесса; отмена отправляет best-effort cancel только для известной `UPLOADING`-сессии.

## Ограничения и важные детали

- Foreground-уведомление прохода (`UploadNotification.ID`) показывает `done/total` по всей eligible-очереди (`UploadRunTally`, Room-запрос `UploadDao.runTally`), а не только по захваченным задачам. Backup, заблокированный политикой сети, в счёт не входит. Ошибки учитываются только те, что возникли в текущем проходе (`UploadRunProgress`). Итог на отдельном `FINISHED_ID` показывается только при полном успехе. На API 36+ уведомление получает `requestPromotedOngoing` и chip; на API 36.0 разрешения `POST_PROMOTED_NOTIFICATIONS` нет, и уведомление остаётся обычным ongoing. Смахивание обрабатывает `UploadNotificationDismissReceiver`, до следующего прохода уведомление не переписывается.
- Состояния Room отражают фазы `QUEUED → HASHING → CREATING_SESSION → UPLOADING → COMPLETING → PROCESSING → ATTACHING → COMPLETED`; ошибки, отмена и `PAUSED` для backup — отдельные состояния.
- Для восстановления используются сохранённые idempotency key и session ID, затем серверный список принятых частей. Локальный `bytesSent` нужен для отображения прогресса; upload-token хранится только в памяти worker и заново выдаётся при resume.
- Если ответ на завершение потерян, worker сверяет серверное состояние перед повтором; если готовый файл не привязался, повтор не передаёт байты заново.
- Отмена задачи помечает её в Room и удаляет staging; worker отменяет связанную серверную сессию при следующем наблюдении задачи. Пауза доступна backup-задачам и проверяется между частями.
- `UploadScheduler` сериализует уникальную WorkManager-очередь: повторный enqueue coalesce-ится для `ENQUEUED`, а enqueue во время `RUNNING` сохраняет successor. Отмена инвалидирует ещё не отправленный запрос перед ожиданием завершения работы.
- `AutoUploadWorker` пакетно сохраняет метаданные всех выбранных фото и видео без общего лимита количества и без hashing во время discovery. В схеме Room новых полей и миграций для lazy staging нет.
- `SessionManager.resetLocalState` сначала отключает и дожидается discovery, отменяет и дожидается upload worker, затем очищает очередь и локальные статусы медиатеки.
- `ProfileViewModel` блокирует перекрывающиеся операции set/remove, держит foreground-индикатор до выхода и не превращает coroutine cancellation в snackbar. Ошибка `SetProfilePicture` оставляет ранее загруженный профильный аватар.
