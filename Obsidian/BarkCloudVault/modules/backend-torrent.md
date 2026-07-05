# Сервис Torrent (BarkCloud.Torrent)

[[index|← Индекс]]

Микросервис загрузки торрентов на **хост-диск** (не в S3) с доступом к скачанному через веб/клиентов. Добавлен 2026-07-05.

## Назначение
- Качает торренты (magnet / .torrent) движком **MonoTorrent 3.0.2** на диск хоста.
- Отслеживает прогресс, скорость, сиды/личи, скачано/отдано, ratio — пер-пользователь.
- Отдаёт скачанные файлы стримингом по Range и умеет импортировать готовый файл в облако (Files/S3).

## Порты
- `TORRENT_PORT=7027` — gRPC (`TorrentApi`, `[Authorize(User)]`).
- `TORRENT_HTTP1PORT=7028` — HTTP1 стриминг файлов по Range (`GET /download/{torrentId}?file=`).
- `TORRENT_PEER_PORT=6881` — BitTorrent peer-порт (TCP+UDP), публикуется на хост.
- БД `torrent` (Postgres), `Torrent:DownloadPath=/mnt/torrents` (том `TORRENT_DOWNLOAD_PATH`).

## Устройство
- `Program.cs` — `LoadConfiguration(ServiceId.Torrent)`, gRPC+XAuth+EF+MassTransit, http1 через `SetRunningAddress` (RunSettings:Http1Port).
- `Infrastructure/TorrentEngineService` — singleton-обёртка `ClientEngine`, словарь Guid→`TorrentManager`, `AutoSaveLoadFastResume`.
- `Infrastructure/TorrentPersistenceService` — тик 5 c: трафик накопительно в БД (переживает рестарт), прогресс/статус/пиры.
- `Infrastructure/TorrentStartupService` — восстановление торрентов из БД при старте (re-add + приоритеты файлов).
- `Infrastructure/TorrentImportService` — импорт файла в облако: `FilesApi.GetUploadUrl` → POST на `cloud-files:{FILES_HTTP1PORT}/upload/{id}` → `CloudApi.AttachFile` (проброс JWT пользователя).
- `Host/TorrentApiService` — gRPC (Add/List/Get/Files/Pause/Resume/Remove/SetFilePriority/ImportToCloud/**StreamProgress** server-streaming).
- `Host/TorrentController` — http1 download (Range, проверка владельца).
- `Consumers/UserDeletedConsumer` — чистит торренты и папку пользователя при удалении аккаунта.

## Живой прогресс (веб)
Браузер `EventSource('/api/torrents/stream')` → [[modules/backend-web]] SSE-эндпоинт → gRPC `StreamProgress` к `cloud-torrent:7027`. WebSocket/SignalR в проекте не используется. Вкладка «Торренты» — `ClientApp/src/pages/TorrentsPage.tsx` + `hooks/useTorrentStream.tsx`.

## Связи
- Контракт: `Shared/BarkCloud.Proto/torrent_api.proto` → [[modules/shared-proto]].
- Конфиг-дефолты (порты/БД/DownloadPath/inter-service) — [[modules/backend-configuration]] (`ServiceId.Torrent=7`).
- Генерация compose/nginx/.env — [[modules/tools-builder]] (тогл `IncludeTorrent`).
