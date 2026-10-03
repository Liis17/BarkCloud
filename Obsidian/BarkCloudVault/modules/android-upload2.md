# Android — Upload 2.0 (возобновляемые сессии)

Parent: [[modules/android-app]] · Сервер: [[modules/upload-2]] · iOS-референс: [[modules/ios-background-upload]] · API: [[api/files-api]]

## Назначение

Все Android-загрузки `CLOUD_FILE` переведены с legacy multipart `/upload/{id}` на
возобновляемые Upload 2.0-сессии (сервер — [[modules/upload-2]]). Control plane —
gRPC `FilesApi`, data plane — `PUT /file-upload/{session}/parts/{n}` напрямую в
Files через nginx **:443** (`BuildConfig.FILES_UPLOAD_BASE`, не :7025). **Аватар
остался на legacy V1** (`UserRepository.setAvatar` → `GetUploadUrl(USER_AVATAR)` →
multipart POST → `SetProfilePicture`) — паритет с iOS.

## Архитектура

| Слой | Файлы |
|---|---|
| Control plane (gRPC) | `net/FileTransferService.kt` — `createUploadSession(idempotencyKey, fileName, fileSize, contentType, sha256)`, `get/resume/complete/cancelUploadSession`; модели `UploadSessionInfo/UploadSessionState/UploadSessionPart` |
| Data plane (OkHttp) | `FileTransferService.uploadSessionPart` + `FileSegmentRequestBody` (окно файла `[offset, offset+size)` через `RandomAccessFile`, без материализации части — в отличие от iOS, где background URLSession требует файловых задач) |
| Геометрия частей | `data/upload/UploadPartMath.kt` — partSize назначает сервер (база 16 MiB, ≤10000 частей), клиент только вычисляет offset/length/missing |
| Хеш | `data/upload/UploadFileHasher.kt` — потоковый SHA-256 блоками 4 MiB; backup приносит готовый хеш из `AutoUploadWorker` |
| Очередь | Room `upload_jobs` v2: + `idempotencyKey`, `sessionId`, `partSize`; `uploadUrl`/`sourceUri` из V1 удалены из entity (колонки остаются в БД как residue) |
| Исполнитель | `data/upload/UploadWorker.kt` — foreground dataSync, до **4 файлов параллельно** (Semaphore), части файла строго последовательно |

Заголовки data-plane: `Content-Type: application/octet-stream`, точный
`Content-Range: bytes {from}-{to}/{total}`, `X-Upload-Token` (access-токен не
кладётся). Успех части подтверждается JSON-квитанцией `{partNumber, size}` со
строгим сверением значений (`PartAckParser`); HTML-страница от кривого proxy не
маскируется под часть.

## Фазы (`UploadPhase`)

```
QUEUED → HASHING → CREATING_SESSION → UPLOADING → COMPLETING → PROCESSING → ATTACHING → COMPLETED
UPLOADED_NOT_ATTACHED — байты на сервере (ready), упало post-ready действие; retry повторяет только attach
PAUSED — только backup (pauseBackup/resumeBackup); терминальные: FAILED / CANCELLED
UPLOADED — мёртвая V1-фаза, оставлена в enum для чтения старых строк до одноразовой миграции
```

Upload-token хранится **только в RAM** воркера; после рестарта процесса
перевыпускается через `ResumeUploadSession`.

## State machine воркера (`process`)

1. **HASHING** — SHA-256 staged-файла (если нет готового из backup). Расхождение
   размера с заявленным → терминальная ошибка.
2. **ATTACHING / UPLOADED_NOT_ATTACHED** — attach напрямую, без обращения к сессии.
3. `obtainSession`: нет `sessionId` → `CreateUploadSession` (ключ идемпотентности
   генерится в enqueue, персистится — обрыв связи до ответа не создаёт дубль
   резерва). `phase == UPLOADING` → `Resume` (token + авторитетный список S3-частей).
   `COMPLETING` → сначала `Get` (complete мог дойти, но ответ потеряться), при
   `uploading` → `Resume`. Терминальное состояние сессии (`failed/expired/cancelled`)
   → **новая сессия с новым idempotency key** (старый связан с мёртвой в `(OwnerId, Key)`).
4. `uploading` → `uploadMissingParts`: skip частей с `hasEtag && size == ожидаемой`;
   каждая часть — 3 попытки, backoff 500мс·2ⁿ; retryable = сеть/408/429/5xx/401
   (протухший token лечится resume на следующем цикле ретрая задачи). Прогресс =
   сумма подтверждённых байт. Между частями — проверка фазы (отмена/пауза из UI).
5. **Complete** (+ровно один recovery-цикл): при ошибке сначала сверка
   `GetUploadSession` (`processing/ready/терминальная` = complete фактически
   сработал); код `09BF4D7B…` (не все части) → resume → дослать недостающее →
   повторить Complete.
6. **PROCESSING** — polling `GetUploadSession` каждые 2 с до `ready/failed/expired`.
7. **ATTACHING** — `AttachFile` с `upload_session_id` и `is_upload_retry` (для
   ретрая после `uploaded_not_attached`); `FileAlreadyAttached`
   (`F1A2B3C4…`) = успех-реплей; затем `albumId` → `AddItemsToAlbum`; удаление
   staging, `MediaCloudState = IN_CLOUD`, для backup — каскад `AutoUploadScheduler.runOnce`.

Квота (`64E94D14…` при create) → терминальный FAILED с локализованным сообщением
«Недостаточно свободного места в облаке».

## Отмена, пауза, retry

- `cancel()` помечает CANCELLED + удаляет staging; работающий воркер замечает фазу
  между частями/в poll и вызывает `CancelUploadSession` (S3 abort + освобождение
  резерва). Отмены мимо воркера подчищаются на его следующем запуске
  (`cancelledWithSessions` → server cancel → `clearSession`).
- Пауза backup (`PAUSED`) — между частями; слот освобождается, задача остаётся.
- Retryable-ошибки: фаза откатывается на безопасную (`sessionId == null` → QUEUED,
  иначе UPLOADING/PROCESSING), воркер возвращает `Result.retry()` (WorkManager
  backoff, ≤5 попыток); при исчерпании — терминально FAILED/UPLOADED_NOT_ATTACHED.
- `retry(id)` из UI: `UPLOADED_NOT_ATTACHED` → ATTACHING; иначе есть сессия →
  UPLOADING (resume сам разберёт серверное состояние), нет → QUEUED.

## Восстановление

После убийства процесса воркер берёт задачи в активных фазах и опирается на
серверный список частей (Resume), а не на локальный `bytesSent` — докачиваются
только отсутствующие части. `bytesSent` в БД — только для UI-прогресса.

## Миграции

- **Room v1→v2** (`BarkCloudDatabase.MIGRATION_1_2`): ALTER TABLE добавляет
  `idempotencyKey`/`sessionId`/`partSize`.
- **Одноразовая V1→V2** (`UploadQueueStore.migrateV1Jobs`, marker в prefs
  `barkcloud_upload2/migration.v1`, паритет с iOS): активные legacy-задачи
  (QUEUED/UPLOADING/UPLOADED/ATTACHING/PAUSED) отменяются со staging-очисткой и
  сообщением «Загрузка отменена при обновлении приложения». Enum `UPLOADED`
  сохранён, чтобы Room-конвертер не падал на старых строках до миграции.
- **Backup теперь всегда стейджится** (`enqueue` без `stageSource`): V2 требует
  SHA-256 до create и повторного чтения регионов файла под части — стримить из
  MediaStore Uri с skip по смещению ненадёжно.

## Нотификация

`ProgressTracker` агрегирует до 4 активных задач: файлы done/total в тексте,
байт-процент в `Notification.ProgressStyle` (API 36+); события из OkHttp-потоков
коаледсятся в conflated-channel, `setForeground` — максимум раз в 250 мс. Прогресс
персистится в Room с тем же троттлингом (без `runBlocking`, который был в V1).

## Отличия от iOS ([[modules/ios-background-upload]])

- Транспорт частей: OkHttp-стриминг окна файла vs материализация части в staging-файл
  (требование `URLSession.background`).
- Оркестрация: WorkManager foreground dataSync vs background URLSession + BGTaskScheduler;
  retry — WorkManager backoff vs BGTask +5мин.
- Параллельность 4 файла — как в iOS; сериальность частей — как в iOS.

## Тесты

`UploadPartMathTest` (геометрия, missing/confirmed по etag+size), `UploadPartHttpTest`
(`PartAckParser` вкл. подмену HTML, retry-классификация HTTP-кодов, `FileSegmentRequestBody`
окно/прогресс), `UploadFileHasherTest`. `ClientMetadataInterceptorTest` падал до
миграции (несвязанная проблема mockk/gRPC-арности).

## Карта файлов

| Файл | Роль |
|---|---|
| `net/FileTransferService.kt` | V2 RPC + `uploadSessionPart` + `FileSegmentRequestBody`/`PartAckParser`/`UploadPartHttpException`; legacy multipart только аватар |
| `data/upload/UploadWorker.kt` | state machine, параллельность, recovery, отмена |
| `data/upload/UploadQueueStore.kt` | enqueue (всегда staging), retry/cancel, одноразовая V1-миграция |
| `data/upload/UploadPartMath.kt` / `UploadFileHasher.kt` / `UploadErrorCodes.kt` | чистые хелперы |
| `data/persistence/UploadDao.kt` / `BarkCloudDatabase.kt` | Room v2 + миграция |
| `data/upload/UploadNotification.kt` | агрегированный прогресс (+percent) |
| `data/cloud/CloudRepository.kt` | `attachFile(+uploadSessionId, isUploadRetry)`; мёртвый `uploadFile` удалён |
| `grpc/GrpcEndpoint.kt` + `app/build.gradle.kts` | `fileUploadBase` / `FILES_UPLOAD_BASE` |
