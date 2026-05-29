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
| `ReadFile(offset)` | `GetTempDownloadUrl` → HTTP GET (Range — фаза 5; пока целиком) |
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
  `UnmountAsync`/`GetStatusAsync`/`ShutdownAsync`) + DTO `EngineStatus`.
- **BarkCloud.Drive.Engine** — фоновый процесс: gRPC-клиенты Identity/Cloud/Files,
  `TokenManager` (логин `Identity.Auth` + проактивный refresh `CreateToken` по
  `expiration_date`), `MetadataInterceptor` (device-заголовки base64 + динамический токен),
  ФС Dokany (из бывшего PoC), `MountManager`, IPC-сервер (named pipe + StreamJsonRpc).
  Один экземпляр на пользователя (Mutex).
- **BarkCloud.Drive.App** — WPF UI: логин/пароль/OTP, выбор свободной буквы, монтирование;
  `EngineLauncher` поднимает процесс движка и коннектится по pipe.

Логин/пароль идут из UI в движок; **движок сам авторизуется и обновляет токен**. Диск
живёт в движке — UI можно закрыть. PoC свёрнут в Engine (один путь монтирования).

**Критично для Auth:** сервер требует device-заголовки `x-device-name/os/app/version`
(иначе `XDeviceNameIsRequired`/`XOsNameIsRequired`/`XAppInfoIsRequied`), все значения —
**Base64(UTF8)**; токен `x-auth-token` — сырой. `x-device-id` персистится
(`%LOCALAPPDATA%\BarkCloud.Drive\device-id`).

Запуск требует драйвера **Dokany 2.x** + бэкенда; адрес — в `Engine/appsettings.json`.

## План фаз

1. ~~Core: gRPC-клиенты + refresh-менеджер~~ ← **сделано** (в Engine: TokenManager + авторефреш)
2. ~~Engine read-only маунт~~ ← **сделано**
3. Запись: CreateFile/WriteFile/Cleanup-upload (+дедуп-file_id)
4. Мутации: Move/Delete/CreateDirectory
5. Бэкенд Range + поблочный кэш содержимого
6. Бэкенд: копии внутри диска
7. UI ← **базовый WPF сделан** (логин/буква/монтирование); осталось трей + автозапуск +
   инсталлятор с бандлом драйвера Dokany
