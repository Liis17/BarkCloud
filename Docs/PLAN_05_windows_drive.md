# Plan 05 — Windows Drive: дубликаты/копирование + не глушить ошибки

> Проект: `Drive/BarkCloud.Drive.Engine` (Dokany-движок, отдельный процесс), `Drive/BarkCloud.Drive.App` (WPF + трей), `Drive/BarkCloud.Drive.Contracts` (IPC-контракты). Сборка: `dotnet build` соответствующих проектов (приложение диска должно быть закрыто — иначе DLL залочены).

## Верификация (без клиентских правок)

- **Дубликаты и копирование чинятся бэкендом Plan 01.** Клиент не считает хеш и не блокирует дубликаты — дедуп был чисто серверным. После снятия дедупа: копия файла внутри X: (Проводник: новый файл → запись новых байт → `PersistSession` → upload) получает **новый `file_id`**, поэтому `AttachFile` больше не падает `FileAlreadyAttachedException` (раньше копия получала `file_id` оригинала и привязка отклонялась). Коллизия имени в папке авто-переименовывается (Plan 01). **Клиентских изменений не требуется** — подтверждено сборкой движка.

## Задача 5.1 — Не глушить ошибки синхронизации в Cleanup

**Проблема:** `BarkCloudFileSystem.Cleanup` ловит исключение `PersistSession` и только пишет в `engine.log` (`EngineLog.Error`) — пользователь видит «успешное» копирование в Проводнике, но при сбое (сеть/квота) файл в облако не попал, и об этом никто не узнаёт. Движок — отдельный процесс, in-process событие App не увидит; используем существующий IPC-канал статуса (`GetStatusAsync`).

**Файлы:** `Drive/BarkCloud.Drive.Contracts/EngineStatus.cs`, `Drive/BarkCloud.Drive.Engine/BarkCloudFileSystem.cs`, `Drive/BarkCloud.Drive.Engine/DriveEngine.cs`, `Drive/BarkCloud.Drive.App/MainWindow.xaml.cs`.

**Шаги:**
1. `EngineStatus` += `LastSyncError` (string?).
2. `BarkCloudFileSystem` += свойство `LastSyncError`: в `Cleanup`-catch при сбое записи (`info.Context is WriteSession`) выставлять понятное сообщение; в `PersistSession` при успехе — сбрасывать в null.
3. `DriveEngine.Status(...)` пробрасывает `LastSyncError = _fs.LastSyncError`.
4. App `Apply(status)` показывает `LastSyncError` в `StatusText` дашборда (если нет более критичной `Error`).

**Проверка:** `dotnet build` Engine + App + Contracts зелёные. Финальный коммит плана; обновить vault `modules/windows-drive.md`.
