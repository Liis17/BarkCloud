# BarkCloud.Drive — десктопный диск (Windows)

Монтирует облако BarkCloud как диск с буквой (`X:`) в проводнике. Два процесса:

| Проект | Что это |
|---|---|
| **BarkCloud.Drive.Contracts** | IPC-контракт (`IDriveEngine` + `EngineStatus`), общий для UI и движка |
| **BarkCloud.Drive.Engine** | Движок: gRPC-клиенты (Identity/Cloud/Files), логин + авторефреш токена, файловая система Dokany, IPC-сервер. Фоновый процесс без окна |
| **BarkCloud.Drive.App** | WPF UI: логин/пароль/OTP, выбор свободной буквы, кнопки монтирования |

UI ↔ Engine — named pipe + StreamJsonRpc. UI передаёт движку логин/пароль; **движок сам авторизуется и обновляет токен** (за минуту до истечения, по `expiration_date`). Диск живёт в движке — UI можно закрыть, диск останется примонтированным.

## Что нужно для запуска

1. **Драйвер Dokany 2.x** — `DokanSetup.exe` из https://github.com/dokan-dev/dokany/releases
2. **Запущенный backend BarkCloud**, доступный с машины
3. Адреса сервисов — в `BarkCloud.Drive.Engine/appsettings.json`. По умолчанию как в iOS-клиенте: `Host: cloud.barkfluff.com`, `IdentityPort: 7020`, `FilesPort: 7025` (Cloud/Files/Album + `/web`), `DangerousAcceptAnyServerCert: true` (self-signed, как iOS `allowSelfSigned`)

## Запуск

```bash
dotnet build Drive/BarkCloud.Drive.slnx
dotnet run --project Drive/BarkCloud.Drive.App
```

UI при первом действии сам поднимет процесс движка (`EngineLauncher`). Дальше: ввести логин/пароль → «Войти» → выбрать букву → «Примонтировать». «Остановить движок» отмонтирует и завершит движок.

## Поток

1. App стартует, при «Войти» подключается к движку по pipe (если движок не запущен — стартует его).
2. `LoginAsync(login, password, otp)` → движок зовёт `Identity.Auth` с device-метадатой (обязательна: `x-device-name/os/app/version`, base64; `x-device-id` персистится в `%LOCALAPPDATA%\BarkCloud.Drive\device-id`), сохраняет токены, запускает фоновый refresh.
3. `MountAsync("X")` → Dokany монтирует read-only том; чтение каталогов/файлов идёт из облака.
4. Токен обновляется автоматически — перемонтирование не нужно.

## Ограничения (осознанные)

- **Только чтение** (запись — фаза 3); том под `DokanOptions.WriteProtection`.
- **Гидрация файла целиком** при первом чтении (HTTP Range — фаза 5); кэш в `%TEMP%\BarkCloudDrive\`.
- Движок без трея/автозапуска (полировка — фаза 7); один экземпляр на пользователя (Mutex).
- В dev UI находит движок по соседнему bin-пути; в проде оба `.exe` кладутся рядом.
