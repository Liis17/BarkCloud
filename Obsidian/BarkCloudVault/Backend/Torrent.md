# Сервис Torrent

Parent: [[Index]]
See also: [[Shared/Proto]] · [[Backend/Configuration]] · [[Backend/SessionRevocation]] · [[Web/WebApp]] · [[Tools/Builder]]

## Назначение

`BarkCloud.Torrent` использует MonoTorrent для загрузки на диск хоста. Торрент и его файлы принадлежат пользователю; в S3 они попадают только после вызова импорта в облако.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Torrent/Program.cs` | регистрация сервиса | gRPC, HTTP/1, БД, очередь и клиенты |
| `Backend/BarkCloud.Torrent/Host/TorrentApiService.cs` | `TorrentApiService` | пользовательские операции и прогресс |
| `Backend/BarkCloud.Torrent/Host/TorrentController.cs` | `Download` | HTTP выдача файла с диска |
| `Backend/BarkCloud.Torrent/Infrastructure/TorrentEngineService.cs` | `TorrentEngineService` | MonoTorrent engine процесса |
| `Backend/BarkCloud.Torrent/Infrastructure/TorrentStartupService.cs` | `TorrentStartupService` | восстановление из БД при старте |
| `Backend/BarkCloud.Torrent/Infrastructure/TorrentPersistenceService.cs` | `TorrentPersistenceService` | запись прогресса и статистики |
| `Backend/BarkCloud.Torrent/Infrastructure/TorrentImportService.cs` | `TorrentImportService` | перенос готовых файлов в Files |
| `Backend/BarkCloud.Torrent/Infrastructure/TorrentMapper.cs` | маппинг статусов и приоритетов | перевод типов MonoTorrent в proto |
| `Backend/BarkCloud.Torrent/Consumers/UserDeletedConsumer.cs` | `UserDeletedConsumer` | очистка данных пользователя |
| `Backend/BarkCloud.Configuration/Infrastructure/ConfigurationDefaultsPopulator.cs` | настройки Torrent | значения портов и БД по умолчанию |
| `Backend/docker-compose.yml` | `cloud-torrent` | volume и настройки контейнера |
| `Tools/BarkCloud.Builder/BackendComposeGenerator.cs` | compose generator | конфигурация Torrent и nginx |
| `Backend/BarkCloud.Web/Endpoints/TorrentApiEndpoints.cs` | `/api/torrents/stream` | SSE-переадресация прогресса |
| `Shared/BarkCloud.Proto/torrent_api.proto` | `TorrentApi` | gRPC-контракт |

## Публичные контракты

`TorrentApi` требует токен типа User и предоставляет:

| Операции | RPC |
|---|---|
| Добавление | `AddMagnet`, `AddTorrentFile` |
| Просмотр | `ListTorrents`, `SearchTorrents`, `GetTorrent`, `ListFiles` |
| Управление | `PauseTorrent`, `ResumeTorrent`, `RemoveTorrent`, `SetFilePriority` |
| Импорт и прогресс | `ImportToCloud`, потоковый `StreamProgress` |

Состояния API: `METADATA`, `DOWNLOADING`, `SEEDING`, `PAUSED`, `COMPLETED`, `ERROR`. Приоритет файла: `SKIP`, `LOW`, `NORMAL`, `HIGH`.

HTTP `GET /download/{torrentId}?file={index}` проверяет владельца через JWT и отдаёт файл с поддержкой Range. Web передаёт поток `StreamProgress` браузеру как SSE на `/api/torrents/stream`.

## Зависимости и взаимодействия

- EF Core/PostgreSQL хранят пользовательские торренты и файлы. MonoTorrent engine живёт в процессе; директория загрузки разделена по `userId`.
- При запуске торренты перечитываются из БД, добавляются в engine и получают сохранённые приоритеты. Fast-resume данные хранятся в cache directory.
- `TorrentPersistenceService` сохраняет прогресс и накопительные значения переданного/полученного трафика каждые 5 секунд.
- Импорт вызывает Files `GetUploadUrl`, отправляет файл на внутренний HTTP `POST /upload/{uploadId}`, затем вызывает `CloudApi.AttachFile`, передавая JWT пользователя.
- MassTransit `UserDeleted` удаляет торренты пользователя из engine, его директорию загрузки и строки БД.
- `StreamProgress` проверяет отзыв сессии во время открытого потока; синхронизация отзывов приходит из Identity — [[Backend/SessionRevocation]].

## Ограничения и важные детали

- `Torrent:DownloadPath` по умолчанию равен `/mnt/torrents`; путь пользователя — подкаталог с его ID. `Torrent:PeerPort` по умолчанию `6881`.
- Configuration задаёт `RunSettings.Port` по умолчанию `7027`, `RunSettings.Http1Port` — `7028`, а ключ БД `TorrentDb` использует имя `torrent` по умолчанию.
- В compose-сервисе настроен peer port для engine, но он не опубликован секцией Docker `ports`; volume загрузок монтируется в `/mnt/torrents`.
- `ImportToCloud` принимает один `file_index` или импортирует все завершённые файлы, если индекс не задан. В proto пустой `directory_id` описан как корень, но импортёр при пустом значении включает `route_by_media_kind`; фактически Files выбирает системную папку по типу файла.
- Импорт передаёт байты через HTTP-клиент `TorrentImportService.HttpClientName`, зарегистрированный `AddFilesUploadClient` со сроком `TransferTimeout` = 2 ч (равен `proxy_read_timeout 7200s` nginx). Отмена приходит только через `CancellationToken` gRPC-вызова `ImportToCloud`; Web (`Endpoints/TorrentApiEndpoints.cs`) отмену браузера в вызов не передаёт, поэтому после закрытия вкладки импорт продолжается.
- Приоритет proto явно преобразуется в MonoTorrent enum, потому что числовые значения этих enum различаются. Изменения приоритетов сохраняются для восстановления после рестарта.
- Pause, resume и remove сначала выполняются в engine и лишь затем отражаются в БД. Ошибка engine не оставляет БД в состоянии, будто операция уже выполнена.
