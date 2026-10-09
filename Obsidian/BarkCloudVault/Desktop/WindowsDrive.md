# Windows Drive — виртуальный диск BarkCloud

Parent: [[Architecture]] · Files API: [[Backend/Files]] · Identity: [[Backend/Identity]] · IPC и proto: [[Shared/Proto]]

## Назначение

Клиент Windows монтирует облачные каталоги как диск с буквой через Dokany. Решение состоит из WPF-приложения `BarkCloud.Drive.App`, скрытого процесса `BarkCloud.Drive.Engine` и общих IPC-контрактов `BarkCloud.Drive.Contracts`.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Drive/BarkCloud.Drive.Contracts/IDriveEngine.cs` | `IDriveEngine` | IPC API приложения и движка |
| `Drive/BarkCloud.Drive.Engine/Program.cs` | точка входа | Конфигурация, восстановление сессии, автомонтирование и named pipe |
| `Drive/BarkCloud.Drive.Engine/DriveEngine.cs` | `DriveEngine` | Реализация IPC и управление сессией/диском |
| `Drive/BarkCloud.Drive.Engine/BarkCloudFileSystem.cs` | `BarkCloudFileSystem`, `WriteSession` | Операции Dokany и commit файлов при Cleanup |
| `Drive/BarkCloud.Drive.Engine/CloudGateway.cs` | `CloudGateway` | gRPC/HTTP, разрешение путей, кеш и передача содержимого |
| `Drive/BarkCloud.Drive.Engine/TokenManager.cs`, `TokenStore.cs` | `TokenManager`, `TokenStore` | Авторизация и хранение refresh-токена |
| `Drive/BarkCloud.Drive.App/EngineLauncher.cs`, `MainWindow.xaml.cs` | `EngineLauncher`, `MainWindow` | Запуск, IPC-подключение, дашборд и автомонтирование UI |

## Публичные контракты

- UI и Engine общаются через named pipe `BarkCloud.Drive.Engine` и StreamJsonRpc. `IDriveEngine` предоставляет login/logout, обычный вход с OTP, вход WebAuthn, mount/remount/unmount, статус, аватар, настройки кеша, язык и shutdown.
- Движок использует отдельные gRPC-каналы Identity, Files/Cloud и Users; байты передаются по HTTP через Files web endpoint. Файлы отправляются legacy multipart-потоком; серверные операции описаны в [[Backend/Files]].
- Адрес сервера задаётся в `server.json` в профиле пользователя. Значения по умолчанию в `Drive/BarkCloud.Drive.Engine/appsettings.json`: `cloud.barkfluff.com`, Identity `:7020`, Users `:7021`, Files `:7025`.
- В настройках можно менять букву диска, метку тома и папку кеша. Смена буквы/метки перемонтирует том; смена папки влияет на новые загрузки и не переносит ранее скачанные файлы.
- WebAuthn-кнопка доступна в мастере при поддержке Windows WebAuthn и доменном имени сервера. Assertion получают через Windows WebAuthn API; регистрация и управление ключами остаются вне этого клиента.

## Зависимости и взаимодействия

- `BarkCloud.Drive.App` — WPF-UI с треем и мастером первого запуска; `Engine` — `WinExe` без окна, который обслуживает Dokany и named pipe. Каждый процесс ограничен одним экземпляром на пользователя.
- `TokenManager` выполняет login и проактивно обновляет access-token через Identity. Access-token хранится в памяти; refresh-token шифруется DPAPI в `%LOCALAPPDATA%/BarkCloud.Drive/refresh.bin`. Стабильный device ID хранится рядом.
- `MetadataInterceptor` добавляет device/app headers в Base64(UTF-8), а `x-auth-token` передаёт как сырой токен. Эти метаданные нужны серверной авторизации.
- Движок восстанавливает сессию при запуске и автоматически монтирует последнюю сохранённую букву, если refresh-токен действителен. UI и Engine могут запускаться независимо через отдельные записи автозагрузки Windows.
- Список каталогов собирается cursor-пагинацией с кешем на 5 секунд. Файлы разрешаются по пути каталога; данные читаются из облака через HTTP.

## Ограничения и важные детали

- Документ открыт для чтения и записи. Чтение запрашивает HTTP Range блоками по 1 МиБ и кеширует блоки в настроенной папке; если сервер не возвращает partial response, клиент скачивает файл целиком.
- Создание и изменение буферизуют файл во временной рабочей копии под `%TEMP%/BarkCloudDrive/write`. Upload и привязка выполняются в Dokany `Cleanup`; изменение содержимого передаёт файл целиком. Ошибки commit видны в `EngineStatus.LastSyncError`.
- Удаление файла фиксируется при `Cleanup` и отправляется движком пакетно; каталоги удаляются через облачный API. Переименование и перемещение используют операции для записи каталога/файла.
- Повторная загрузка идентичного содержимого создаёт отдельный blob: текущая реализация backend намеренно отключает дедупликацию оригиналов. Привязка одного и того же file ID повторно не допускается; подробности модели — в [[Backend/Files]].
- В конфигурации по умолчанию `DangerousAcceptAnyServerCert` включён. При активном флаге gRPC и HTTP transport принимают любой сертификат сервера; доверие к хосту не проверяется.
- Dokany 2.x должен быть установлен на Windows-машине. Папка рабочей копии для записи находится в системном temp; кеш скачанного содержимого настраивается отдельно.
