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
- **Бэкенд дорабатывается** под нужды диска: планируется HTTP **Range** на
  `/download/{tempFileId}` (ленивые частичные чтения больших файлов) и ослабление
  инварианта *one-entry-per-file* в `AttachFileCommandHandler` для копий внутри диска.
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
| `Cleanup` (`DeletePending`) | `DeleteFileEntry` / `DeleteDirectory` |
| `MoveFile` | `Rename*`/`Move*` Directory/FileEntry |
| `DeleteFile`/`DeleteDirectory` | проверка возможности (реальное удаление — в `Cleanup`) |

Контракт, который легко перепутать: реальное удаление — в `Cleanup` при
`info.DeletePending`; аплоад — в `Cleanup`, не в `WriteFile`.

## Семантические несостыковки с бэкендом (медиа-облако ≠ POSIX-ФС)

- Блобы **иммутабельны** и нет частичной записи → правка = перезалив файла целиком.
- Дедуп по SHA256: `file_id` из `GetUploadUrl` **может отличаться** от итогового →
  `AttachFile` звать с id из ответа аплоада.
- one-entry-per-file ломает копирование внутри диска (см. правку бэкенда).
- Долгий маунт + access-токен ~30 мин → нужен фоновый авторефреш (`Identity.CreateToken`),
  сериализация refresh, стабильный `device_id`.

## Текущее состояние

Решение **`Drive/BarkCloud.Drive.slnx`** — два процесса (read-only), собирается чисто:

- **BarkCloud.Drive.Contracts** — IPC-контракт `IDriveEngine` (`LoginAsync`/`MountAsync`/
  `UnmountAsync`/`GetStatusAsync`/`GetSettingsAsync`/`SetCacheDirAsync`/`ShutdownAsync`) +
  DTO `EngineStatus`, `EngineSettings` (папка кэша).
- **BarkCloud.Drive.Engine** — **скрытый `WinExe`** (без окна / панели задач / Alt-Tab):
  gRPC-клиенты Identity (:7020) / Cloud+Files (:7025) двумя каналами как в iOS,
  `TokenManager` (логин `Identity.Auth` + проактивный refresh `CreateToken` по
  `expiration_date`), `TokenStore` (refresh-токен в **DPAPI**, восстановление сессии на старте),
  `MetadataInterceptor` (device-заголовки base64 + динамический токен),
  ФС Dokany (из бывшего PoC), `MountManager`, IPC-сервер (named pipe + StreamJsonRpc).
  Один экземпляр на пользователя (Mutex). Download-URL нормализуется на актуальный Files-эндпоинт.
- **BarkCloud.Drive.App** — UI на **WPF-UI** (`FluentWindow`) + **трей** (`Wpf.Ui.Tray.NotifyIcon`):
  логин/пароль/OTP, выбор свободной буквы, монтирование. Трей: Открыть / Примонтировать /
  Отмонтировать / Закрыть приложение. `EngineLauncher` поднимает движок и коннектится по pipe.
  Закрытие окна → в трей (диск жив); «Закрыть приложение» → unmount + stop движка + выход.
  `AppSettings` хранит последнюю букву диска (`%LOCALAPPDATA%\BarkCloud.Drive\app.json`).
  Секция «Папка кэша» (`OpenFolderDialog` → `SetCacheDirAsync`): выбор каталога кэша диска.

**Папка кэша** — забота движка (он владеет кэшем). `EngineSettingsStore` хранит путь в
`%LOCALAPPDATA%\BarkCloud.Drive\settings.json` (по умолчанию `%TEMP%\BarkCloudDrive`), читается на
старте в `CloudGateway`. Смена на лету (`CloudGateway.SetCacheDir`): новые чтения идут в новую папку,
ранее скачанное остаётся в прежней (не переносится). UI выбирает папку, путь идёт в движок по IPC.

Логин/пароль идут из UI в движок; **движок сам авторизуется, хранит refresh в DPAPI и обновляет
токен**, восстанавливает сессию на старте. Диск живёт в движке — UI можно закрыть. PoC свёрнут в Engine.
Адреса сервисов — `Engine/appsettings.json` (`Host`/`IdentityPort`/`FilesPort`, как iOS).

**Автостарт UI (`MainWindow.InitializeAsync` на `ContentRendered`):** подключиться к движку
(поднять, если не запущен) → `GetStatusAsync`. Если сессия уже восстановлена из `refresh.bin`
(`Authenticated`) — **форма входа сворачивается** (`LoginPanel.Visibility`), повторный логин не нужен,
и диск **автомонтируется** на запомненную букву (иначе первую свободную). Автомонтаж только на старте и
после ручного логина — НЕ из 3-сек polling (чтобы не перемонтировать после ручного unmount).
Пустой логин отсекается и в UI, и в `DriveEngine.LoginAsync` → сервер `Auth` больше не бросает
`NotSetUsernameOrEmailException` («Не передан ни логин ни email») вхолостую.

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
