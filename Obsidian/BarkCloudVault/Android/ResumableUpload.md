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
2. `UploadWorker` обходит активную очередь keyset-страницами и обрабатывает не больше четырёх задач одновременно. Для backup перед копированием повторно проверяет включённую автозагрузку, выбранную папку и разрешение. `BackupStager` потоково пишет собственный `.part`, проверяет имя, MIME, размер и дату изменения до/после чтения, считает SHA-256 фактических staged-байтов и атомарно публикует файл. В очереди одновременно удерживается не более четырёх app-private backup-копий; при повторе сохранённая копия и серверная сессия используются, если фактические байты не изменились.
3. `UploadWorker` создаёт или восстанавливает серверную сессию и отправляет только неподтверждённые части. Размер части назначает сервер; тело каждой части читает соответствующее окно staging-файла через `RandomAccessFile`, не загружая файл целиком в память.
4. Подтверждённая сервером часть учитывается, только если у неё есть ETag и размер совпадает с ожидаемым. Части одного файла отправляются последовательно; параллельно обрабатываются максимум четыре файла.
5. После завершения передачи клиент согласует состояние сессии, ожидает обработки сервером и привязывает готовый file ID к каталогу или системному разделу. Если указан альбом, после привязки он добавляется отдельно. Фаза `ATTACHING`/`UPLOADED_NOT_ATTACHED` обрабатывается до требования staging-файла, поэтому повтор только привязки не копирует исходник.
6. `UploadQueueStore` удаляет staging после завершения. Ошибка после готовности файла сохраняет фазу `UPLOADED_NOT_ATTACHED`; повтор задачи выполняет только привязку. Ошибка одного недоступного источника завершает только его строку; сетевой сбой, quota, ENOSPC или потеря разрешения останавливают захват новых задач и оставляют хвост очереди `QUEUED`.
7. WorkManager запускает общий foreground data-sync worker при доступной сети. Discovery запускает обработку также при нуле новых элементов, если в Room уже есть активная очередь. Уникальная одноразовая работа coalesce-ит запросы в состоянии `ENQUEUED` и добавляет одного successor при `RUNNING`; смена политики сети заменяет устаревший запрос только при изменении сохранённой политики. `DeviceFoldersScreen` запускает discovery при включении папки, backup-файлы отправляются в `SYSTEM_BY_MEDIA_KIND`.

Отдельный foreground-путь аватара не использует Room или WorkManager. Он запрашивает настоящее имя через `OpenableColumns.DISPLAY_NAME`, MIME — через resolver с fallback `application/octet-stream`, считает SHA-256 и размер staged-файла, отклоняет пустой файл и размер свыше 20 MiB, затем передаёт части последовательно. Состояние после `CompleteUploadSession` опрашивается каждые 2 секунды не дольше 120 секунд; профиль меняется только для `READY` с непустым `file_id`. Cache-файл удаляется после успеха, ошибки или отмены. Аватарный upload не восстанавливается после уничтожения процесса; отмена отправляет best-effort cancel только для известной `UPLOADING`-сессии.

## Ограничения и важные детали

- Состояния Room отражают фазы `QUEUED → HASHING → CREATING_SESSION → UPLOADING → COMPLETING → PROCESSING → ATTACHING → COMPLETED`; ошибки, отмена и `PAUSED` для backup — отдельные состояния.
- Для восстановления используются сохранённые idempotency key и session ID, затем серверный список принятых частей. Локальный `bytesSent` нужен для отображения прогресса; upload-token хранится только в памяти worker и заново выдаётся при resume.
- Если ответ на завершение потерян, worker сверяет серверное состояние перед повтором; если готовый файл не привязался, повтор не передаёт байты заново.
- Отмена задачи помечает её в Room и удаляет staging; worker отменяет связанную серверную сессию при следующем наблюдении задачи. Пауза доступна backup-задачам и проверяется между частями.
- `UploadScheduler` сериализует уникальную WorkManager-очередь: повторный enqueue coalesce-ится для `ENQUEUED`, а enqueue во время `RUNNING` сохраняет successor. Отмена инвалидирует ещё не отправленный запрос перед ожиданием завершения работы.
- `AutoUploadWorker` пакетно сохраняет метаданные всех выбранных фото и видео без общего лимита количества и без hashing во время discovery. В схеме Room новых полей и миграций для lazy staging нет.
- `SessionManager.resetLocalState` сначала отключает и дожидается discovery, отменяет и дожидается upload worker, затем очищает очередь и локальные статусы медиатеки.
- `ProfileViewModel` блокирует перекрывающиеся операции set/remove, держит foreground-индикатор до выхода и не превращает coroutine cancellation в snackbar. Ошибка `SetProfilePicture` оставляет ранее загруженный профильный аватар.
