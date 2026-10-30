# Backend — варианты видео

Parent: [[Index]] · See also: [[Backend/Files]] · [[Backend/Configuration]] · [[Api/FilesClientGuide]] · [[Platform/Infrastructure]] · [[Web/WebApp]]

## Назначение

Files создаёт сжатые AV1-варианты видео в контейнере WebM. Оригинал остаётся доступен; веб-клиент получает оригинал и готовые варианты через временные URL и позволяет выбрать источник в плеере. Android, iOS, Drive и Mac не зависят от нового контракта.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Files/Services/VideoVariantWorker.cs` | `VideoVariantWorker` | Очередь обработки, аренда, fencing, загрузка и восстановление |
| `Backend/BarkCloud.Files/Services/VideoVariantLadder.cs` | `VideoVariantLadder`, `VideoVariantFilter` | Лестница качеств и ограничения источника |
| `Backend/BarkCloud.Files/Infrastructure/VideoTranscoderClient.cs` | `VideoTranscoderClient` | Авторизованные задания sidecar |
| `Backend/BarkCloud.Files/Infrastructure/VideoTranscodeLeaseManager.cs` | `VideoTranscodeLeaseManager` | PostgreSQL-аренда с поколением владельца |
| `Backend/BarkCloud.Files/Services/VideoQualityLinkBuilder.cs` | `VideoQualityLinkBuilder` | Один пакетный запрос вариантов и временных ссылок |
| `Backend/BarkCloud.Files/Domain/VideoVariant.cs` | `VideoVariant` | Связь оригинала и blob-варианта |
| `Backend/BarkCloud.Files/Domain/VideoTranscodeState.cs` | `VideoTranscodeState` | Состояние, попытки, пропущенные качества и незавершённое намерение |
| `Backend/BarkCloud.Files/Domain/VideoTranscodeLease.cs` | `VideoTranscodeLease` | Единственная аренда очереди |
| `Backend/BarkCloud.Transcoder/Program.cs` | HTTP API | Авторизация и управление заданиями ffmpeg |
| `Backend/BarkCloud.Transcoder/TranscodePaths.cs` | `TranscodePaths` | Проверка путей внутри рабочего каталога |
| `Backend/BarkCloud.Transcoder/FfmpegCommand.cs` | `FfmpegCommand` | Аргументы AV1/WebM без пользовательских аргументов |
| `Backend/BarkCloud.Files/Persistence/Migrations/20261029231625_AddVideoVariantsAndTranscodeState.cs` | EF migration | Таблицы вариантов, состояний и аренды |

## Данные и поток обработки

`UploadFileType.VideoVariant=3` хранит каждый WebM как обычную запись блоба с `StorageProfileId`, владельцами и размером. `VideoVariants` связывает его с исходным `CloudFile` по качеству. `VideoTranscodeStates` хранит состояние одного оригинала, число неудачных попыток (после трёх обработка прекращается), пропущенные высоты и единственное незавершённое намерение `{PendingVariantFileId, PendingProfileId}`. `VideoTranscodeLeases` содержит владельца, поколение и срок аренды.

`VideoVariants:Enabled` по умолчанию выключен. Включённый worker выбирает готовые видео `CloudFile` с владельцами, сортирует от новых к старым и применяет необязательные ограничения `MaxSourceSizeMb` и `MaxDurationMinutes`. Пустой лимит означает отсутствие ограничения. Лестница — 360, 720, 1080, 1440 и 2160; для источника остаются только ступени меньше короткой стороны кадра, уже сохранённые и пропущенные ступени повторно не запускаются. Вариант не сохраняется, если его файл не меньше оригинала.

Worker получает PostgreSQL-аренду на две минуты, обновляет её каждые 30 секунд и увеличивает `Generation` при новом владельце. Каждая запись состояния или метаданных проверяет актуальное поколение под блокировкой; потеря heartbeat отменяет цикл. При takeover новый владелец сначала отменяет активное задание sidecar, затем очищает оставшиеся намерения и рабочую папку.

Оригинал и результаты обрабатываются в общей папке `/mnt/transcode`, затем вариант потоком загружается в S3-профиль `video-variants` (fallback — `universal`). Перед S3 PUT Files долговечно сохраняет намерение и начинает `StorageMigrationGate.TrackUploadAsync`; барьер удерживается до фиксации записи или компенсационного удаления. В одной транзакции Files повторно проверяет generation и живость оригинала, добавляет `UploadFile` и `VideoVariants` с текущим списком владельцев оригинала и очищает намерение. Если процесс падает после PUT, новый цикл удаляет незарегистрированный объект; при ошибке удаления намерение остаётся для повторной сверки. Purge оригинала удаляет связи и варианты.

Варианты исключены из пользовательских листингов, поиска, альбомов и умных папок. Их размер всё равно включается в `UsedBytes` каждого владельца, поэтому видео может увеличить использование выше квоты; новые загрузки продолжает блокировать обычная проверка квоты. Идентификаторы блобов вариантов клиентам не выдаются, а прямой анонимный `/download/{variantId}` отклоняется.

## Transcoder sidecar

`cloud-transcoder` — минимальный ASP.NET Core-сервис без БД, RabbitMQ и Configuration; слушает порт 8080 только в Docker-сети. `X-Transcoder-Token` проверяется сравнением с постоянным временем. `POST /jobs` принимает относительные `input`, `output`, целевую ступень и `generation`; возвращает 202 или 409, когда уже работает задание. `GET /jobs`, `GET /jobs/{id}` возвращают текущий статус и прогресс; `DELETE /jobs/{id}` отменяет процесс ffmpeg.

Dockerfile собирает приложение на SDK 10 и запускает на `aspnet:10.0-noble`, копируя `ffmpeg` и `ffprobe` из `mwader/static-ffmpeg:latest`. Оба контейнера используют UID 1654 и общий хост-каталог, смонтированный в `/mnt/transcode`.

`TranscodePaths` принимает только относительные пути в `/mnt/transcode`, отклоняет `..`, абсолютные пути, двоеточия и симлинки, ведущие за корень. Sidecar сам формирует аргументы: видео кодируется `libsvtav1`, `yuv420p10le`, preset 8, аудио — `libopus` 128 кбит/с, контейнер — WebM. CRF зависит от ступени (360: 36, 720: 34, 1080: 32, 1440: 30, 2160: 28). `TRANSCODER_THREADS` ограничивает `-threads` и SVT-AV1 parallelism; CPU и память ограничиваются Docker compose.

Задания хранятся только в памяти sidecar. После его перезапуска Files увидит пропавшее задание, повторит обработку и восстановит состояние из PostgreSQL. Общий том и token задаются в `Backend/docker-compose.yml`, шаблонные значения — в `Backend/sample.env`; отсутствие или `replace-*` значение токена восстанавливает `Backend/migrate-secrets.sh`.

## API качества и веб

`VideoQualityInfo` содержит ступень качества, ширину, размер, признак `is_original` и временный `url`. Контракт добавлен в ответы `GetTempDownloadUrl`, `GetSharedFileDownloadUrl`, `ResolveShare` и публичные `PublicFileEntry` (файлы в публичных папках, альбомах и общей директории). Files получает список вариантов пачкой и создаёт temp-ссылки одним batch-вызовом; blob-id вариантов остаются серверными.

Web добавляет варианты в `/api/files/download`, `/api/shared/download`, `/api/shared/dir` и ответы публичных ссылок. `VideoPlayer` выбирает источник, сохраняет время и состояние паузы при переключении, помнит выбор в cookie `bc_video_quality` и предлагает AV1 только при поддержке браузером `video/webm; codecs="av01, opus"`. Если AV1 не поддерживается, доступен оригинал. Контракт клиента — [[Api/FilesClientGuide]], веб-поведение — [[Web/WebApp]].

## Ограничения

- Транскодирование выполняется на CPU и может быть долгим; количество потоков, CPU, RAM, максимальный размер и длительность источника ограничивают нагрузку.
- Тонмаппинг HDR не выполняется, поэтому цвета варианта могут отличаться от оригинала.
- Браузер без AV1/WebM получает только доступный оригинал.
- Настройки очереди Configuration применяются после перезапуска Files; запуск ffmpeg остаётся выключен до `VideoVariants:Enabled=true`.
- Сайдкар изолирован Docker-сетью и рабочей директорией, но любой участник сети с `TRANSCODER_TOKEN` может запускать задания внутри этой папки.
