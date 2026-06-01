[[index]]

# Windows Drive — десктопный виртуальный диск (BarkCloud.Drive)

> Десктопное приложение под Windows: монтирует облако BarkCloud как диск с буквой
> (`X:`) в проводнике. Файлы на диске — это записи облака; содержимое подкачивается
> по запросу. Начато: 2026-05-29.

## Ключевые решения

- **Движок ФС: Dokany** (NuGet `DokanNet`), не WinFsp. Причина: продукт **закрытый**,
  а WinFsp под GPLv3 требует платной коммерческой лицензии (~$6000/3г); Dokany под
  MIT/LGPL — бесплатен и тоже даёт настоящую букву диска. CfAPI/ProjFS отпали —
  дают только папку, не букву.
- **Бэкенд дорабатывается** под нужды диска: HTTP **Range** на
  `/download/{tempFileId}` (ленивые частичные чтения больших файлов). Копирование внутри диска
  решено снятием серверного дедупа (Plan 01) — инвариант *one-entry-per-file* ослаблять не пришлось.
- Транспорт: gRPC к [[api/files-api]] (`CloudApi`, `FilesApi`), токен в метадате
  `x-auth-token` (см. [[modules/shared-auth]], `JwtClientInterceptor`). Сами байты —
  по HTTP: `POST /upload/{fileId}`, `GET /download/{tempFileId}` (порт 7006).

## Карта `IDokanOperations` → BarkCloud

| Колбэк Dokany | BarkCloud |
|---|---|
| `GetDiskFreeSpace` | `FilesApi.GetUserStorageInfo` (limit / used) |
| `GetVolumeInformation` | метка `BarkCloud`, read-only том |
| `FindFiles` | `CloudApi.ListDirectoryDetailed` (через резолв пути→GUID) |
| `GetFileInformation` | из кэша листинга |
| `CreateFile` (open) | резолв `entryId`/`fileId`, без скачивания |
| `ReadFile(offset)` | поблочно (1 МиБ) по HTTP **Range** к temp-URL; fallback на целиком |
| `Cleanup` (запись) | `GetUploadUrl` → `POST /upload` → `AttachFile` (фаза 3) |
| `Cleanup` (`DeletePending`) | файл → `QueueDeleteFileEntry` (батч `DeleteFileEntries`); папка → `DeleteDirectory` |
| `MoveFile` | `Rename*`/`Move*` Directory/FileEntry |
| `DeleteFile`/`DeleteDirectory` | проверка возможности (реальное удаление — в `Cleanup`) |

Контракт, который легко перепутать: реальное удаление — в `Cleanup` при
`info.DeletePending`; аплоад — в `Cleanup`, не в `WriteFile`.

## Семантические несостыковки с бэкендом (медиа-облако ≠ POSIX-ФС)

- Блобы **иммутабельны** и нет частичной записи → правка = перезалив файла целиком.
- **Дедуп по SHA256 снят** (Plan 01): каждая загрузка = свой `file_id`, поэтому копирование файла
  внутри X: работает (копия получает новый `file_id`, `AttachFile` не падает
  `FileAlreadyAttachedException`; коллизия имени авто-переименовывается). Клиентских правок не
  потребовалось; `AttachFile` по-прежнему звать с `file_id` из ответа аплоада.
- **Ошибки синхронизации в `Cleanup` не глушатся**: при сбое `PersistSession` движок выставляет
  `BarkCloudFileSystem.LastSyncError` → `EngineStatus.LastSyncError` → показ в дашборде App (`StatusText`).
- Долгий маунт + access-токен ~30 мин → нужен фоновый авторефреш (`Identity.CreateToken`),
  сериализация refresh, стабильный `device_id`.

## Текущее состояние

Решение **`Drive/BarkCloud.Drive.slnx`** — два процесса (read-write), собирается чисто:

- **BarkCloud.Drive.Contracts** — IPC-контракт `IDriveEngine` (`LoginAsync`/`LogoutAsync`/
  `MountAsync(letter,label)`/`RemountAsync(letter,label)`/`UnmountAsync`/`GetStatusAsync`/
  `GetAvatarAsync`/`GetSettingsAsync`/`SetCacheDirAsync`/`ShutdownAsync`) + DTO `EngineStatus`
  (+`Username`/`ServerHost`/`VolumeLabel`), `EngineSettings` (папка кэша).
- **BarkCloud.Drive.Engine** — **скрытый `WinExe`** (без окна / панели задач / Alt-Tab):
  gRPC-клиенты Identity (:7020) / Cloud+Files (:7025) / **Users (:7021)** каналами как в iOS,
  `TokenManager` (логин `Identity.Auth` + проактивный refresh `CreateToken`; `Logout()` чистит токены+refresh.bin),
  `TokenStore` (refresh-токен в **DPAPI**, восстановление сессии на старте),
  `UserProfile` (имя + URL аватара через `UsersApi.GetUser(0)`, кэш до logout; почту клиентский API не отдаёт;
  аватар — `profile_picture_preview`→full, готовый /download-URL, качается напрямую `CloudGateway.DownloadAvatarAsync`),
  `MetadataInterceptor` (device-заголовки base64 + динамический токен),
  ФС Dokany (метка тома `VolumeLabel` settable → имя диска), `MountManager`, IPC-сервер (named pipe + StreamJsonRpc).
  Один экземпляр на пользователя (Mutex). Download-URL нормализуется на актуальный Files-эндпоинт.
  `EngineSettingsStore` помнит последний маунт (буква+метка); **Program на старте автомонтирует** его при
  восстановленной сессии (для автозапуска движка без UI); `MountAsync` идемпотентен (гонка авто-монтажа движка и UI).
- **BarkCloud.Drive.App** — UI на **WPF-UI** (`FluentWindow`) + **трей**. Три окна:
  - **`FirstRunWizard`** — мастер первого запуска (по `AppSettings.Configured`): **адрес сервера** (хост+порты+cert)
    → логин → имя диска+буква → папка кэша → **автозагрузка** (чекбоксы UI/движок) → создание диска
    (`SetCacheDirAsync`→`MountAsync(letter,label)`). Шаг адреса сохраняет `server.json` и **перезапускает движок**
    (`RestartEngineAsync`, делегат от `MainWindow`) — каналы строятся только на старте; мастер берёт свежий прокси.
  - **`MainWindow`** (дашборд) — **аватар** (круглый, `Ellipse`+`ImageBrush`, байты через `GetAvatarAsync`) + имя
    пользователя + сервер, прогресс хранилища, баннер «движок не запущен» (+ «Запустить движок»), кнопка «Настройки».
    Опрос статуса **5 c только когда окно видимо** (пауза в трее/свёрнуто, обновление при возврате —
    `StateChanged`+`IsVisibleChanged`). Трей: Открыть/Примонтировать/Отмонтировать/Закрыть.
  - **`SettingsWindow`** (модаль) — разлогин, **адрес сервера** (блок «Сервер»: хост+порты+cert → `server.json` + перезапуск
    движка; если сессия не восстановилась — `Configured=false`+закрытие → мастер), монтаж/размонтаж, переименование
    и смена буквы (через `RemountAsync`), папка кэша, **автозагрузка** (чекбоксы), перезапуск движка (`MainWindow.RestartEngineAsync`).
  `EngineLauncher` поднимает движок и коннектится по pipe (`KillEngine()` для рестарта; `EnginePath` для автозагрузки).
  **Автозагрузка** — `Autostart` (HKCU\…\Run): запись App (`--tray` → старт в трей) и/или Engine.
  `AppSettings` (`%LOCALAPPDATA%\BarkCloud.Drive\app.json`): `Configured`, `DriveName`, `DriveLetter`. `DriveLetters.Free()` — свободные буквы.

**Папка кэша** — забота движка (он владеет кэшем). `EngineSettingsStore` хранит путь в
`%LOCALAPPDATA%\BarkCloud.Drive\settings.json` (по умолчанию `%TEMP%\BarkCloudDrive`), читается на
старте в `CloudGateway`. Смена на лету (`CloudGateway.SetCacheDir`): новые чтения идут в новую папку,
ранее скачанное остаётся в прежней (не переносится). UI выбирает папку, путь идёт в движок по IPC.

Логин/пароль идут из UI в движок; **движок сам авторизуется, хранит refresh в DPAPI и обновляет
токен**, восстанавливает сессию на старте. Диск живёт в движке — UI можно закрыть. PoC свёрнут в Engine.
Адреса сервисов (self-hosted) задаёт пользователь в UI → `ServerConfig` (Contracts, общий тип) в
`%LOCALAPPDATA%\BarkCloud.Drive\server.json` (`Host`/`IdentityPort`/`FilesPort`/`UsersPort`/`AcceptAnyCert`).
Движок на старте (`Program.LoadConfig`) накладывает `server.json` поверх дефолтов `Engine/appsettings.json`
(нет файла → дефолты, как iOS). Хелперы валидации полей — `ServerInput` (App).

**Старт UI (`MainWindow.InitializeAsync` на `ContentRendered`):** подключиться к движку (поднять, если не
запущен). Если `!AppSettings.Configured` → **мастер** (`FirstRunWizard.ShowDialog`); отмена первичной настройки
закрывает приложение. Иначе `GetStatusAsync` → дашборд; при `Authenticated && !Mounted` диск **автомонтируется**
на запомненную букву (`MountAsync(DriveLetter, DriveName)`). Разлогин из настроек ставит `Configured=false` →
по закрытии модалки `MainWindow` снова показывает мастер. Пустой логин отсекается в `DriveEngine.LoginAsync`
(сервер `Auth` не бросает `NotSetUsernameOrEmailException` вхолостую). Имя/буква диска = метка тома + точка
монтирования Dokany, поэтому переименование/смена буквы делаются перемонтированием (`RemountAsync`).

**Диск read-write.** Запись: рабочая копия на диске → на `Cleanup` `GetUploadUrl` → multipart
`POST /upload` (поле `file` + `x-auth-token`, как iOS) → эффективный `fileId` из JSON → `AttachFile`.
Правка существующего: гидрация копии, на закрытии перезалив + замена записи (если содержимое менялось;
если `fileId` совпал — no-op). mkdir/delete/rename/move через CloudApi.

**Поблочное чтение (Range, фаза 5).** Чтение больше НЕ гидрирует файл целиком.
- *Бэкенд (сервис Files):* `S3Uploader.DownloadRangeAsync` (`GetObjectRequest.ByteRange` → MinIO/S3 нативно);
  `DownloadFileCommand`/`Result` несут диапазон и `IsPartial`/`TotalSize`/`ContentLength` (длина куска — из
  ответа S3, не из БД, чтобы не разъехался `Content-Length`); `FilesController.DownloadFile` парсит заголовок
  `Range`, отдаёт **206** с `Content-Range`/`Content-Length`/`Accept-Ranges` (один диапазон `bytes=from-[to]`;
  multi/suffix — целиком). temp-URL живёт 60 мин и не одноразовый → по ней можно слать много Range-запросов.
- *Клиент (`CloudGateway.Read(fileId, fileLength, …)`):* режет запрос на блоки по **1 МиБ**, недостающие тянет
  `Range`-GET'ом по кэшированной temp-URL (TTL 50 мин), кладёт блоки файлами `{fileId}.blocks/{N}.blk`
  (`.part`→rename), собирает ответ из блоков. Если сервер вернул не 206 — `RangeNotSupportedException` →
  откат на скачивание целиком (`ReadWhole`, прежний путь; он же — для гидрации копии при записи).
  `fileLength` берётся из листинга (`CloudFile.FileSize`).

**Батчинг удаления.** Удаление множества файлов раньше слало по одному `DeleteFileEntry` на файл.
- *Бэкенд (сервис Files):* новый gRPC `CloudApi.DeleteFileEntries(entry_ids[]) → DeleteFileEntriesResponse{deleted_count}`
  (`Features/Cloud/DeleteFileEntries`, storage `GetLiveFileEntriesByIds`) — массовый soft-delete в корзину, та же
  семантика, что единичный (`IsDeleted/DeletedAt/PurgeAt`). Чужие/несуществующие/уже удалённые id молча
  пропускаются → **идемпотентен** (ретрай после ложного дедлайна не падает). `CloudApiService.DeleteFileEntries`
  парсит Guid через `TryParse`. Удаляет по **entry_id** (запись каталога), не по fileId-блобу — как единичный путь.
- *Клиент (`CloudGateway`):* `DeleteNode` для файла зовёт `QueueDeleteFileEntry(entryId, parentDirId)`. Удаления
  копятся в `_delPending` и уходят пачкой по `DeleteFileEntries`: по таймеру (**1 c окно тишины**) либо по порогу
  **100**, чанками по 100. Буферизованная запись сразу прячется из листингов «тумбстоном» (`_delTombstones`,
  фильтр `HidePending`) — файл не мелькает в Проводнике до отправки. Замена контента в `PersistSession` —
  по-прежнему немедленный единичный `DeleteFileEntry` (не батчится).
- *Надёжность (после состязательного ревью):* все отправки сериализованы `_sendLock` (взятие batch — под ним,
  чтобы выход не проскочил мимо in-flight отправки); `SendNow(allowRetry, drainAll)` — фон делает один проход с
  ретраем (`RequeueFailed`, до 3 попыток, потом `DropOne` возвращает файл в листинг), выход (`FlushPending`)
  дренирует до пустого best-effort. Дренаж форсится на путях выхода `DriveEngine.Logout/Unmount/Shutdown` (в
  Logout — до сброса токена, в Shutdown — до `lifetime.Cancel`) и в `DeleteDirectory`/`Dispose` — иначе удаления в
  окне тишины терялись бы. gRPC-**дедлайн 5 c** на `DeleteFileEntries`, чтобы выход не висел при недоступном
  сервере. Кэш листинга защищён «поколениями» (`_listGen`+`_listLock`): устаревший ответ листинга, начатый до
  коммита удаления, не перезаписывает кэш (иначе удалённый файл «возвращался» бы на TTL=5 c).

**UI:** меню «⋯» сверху (запуск / принудительная остановка движка — `ShutdownAsync` + kill процесса).
**Single-instance:** движок — Mutex; UI — Mutex + named `EventWaitHandle` (второй экземпляр сигналит
первому показать окно и выходит).

**Критично для Auth:** сервер требует device-заголовки `x-device-name/os/app/version`
(иначе `XDeviceNameIsRequired`/`XOsNameIsRequired`/`XAppInfoIsRequied`), все значения —
**Base64(UTF8)**; токен `x-auth-token` — сырой. `x-device-id` персистится
(`%LOCALAPPDATA%\BarkCloud.Drive\device-id`).

Запуск требует драйвера **Dokany 2.x** + бэкенда; адрес — в `Engine/appsettings.json`.

## План фаз

1. ~~Core: gRPC-клиенты + refresh-менеджер~~ ← **сделано** (в Engine: TokenManager + авторефреш)
2. ~~Engine read-only маунт~~ ← **сделано**
3. ~~Запись: CreateFile/WriteFile/Cleanup → upload(multipart "file")+AttachFile, эффективный file_id~~ ← **сделано**
4. ~~Мутации: Move/Delete/CreateDirectory~~ ← **сделано** (rename/move/delete entry+dir, mkdir; правка существующего = перезалив+замена записи)
5. ~~Бэкенд Range + поблочный кэш чтения~~ ← **сделано** (см. ниже «Поблочное чтение»)
6. Бэкенд: копии внутри диска (one-entry-per-file блокирует копию идентичного блоба)
7. UI ← **WPF-UI + трей + скрытый движок + DPAPI-токен + автовосстановление сессии и
   автомонтирование на старте UI сделаны**; осталось автозапуск (вход в систему) +
   инсталлятор с бандлом драйвера Dokany + (опц.) тост-уведомления

**Диагностика:** `EngineLog` пишет в `%LOCALAPPDATA%\BarkCloud.Drive\engine.log` (+ `Debug.WriteLine`);
для `RpcException` печатает `gRPC {StatusCode}: {Detail}`. Успешная запись логируется
(`Сохранён файл … (fileId=…)`). Движок — `WinExe` без консоли, поэтому файл — основной канал.
