# Windows Drive — виртуальный диск BarkCloud

Parent: [[Architecture]] · Files API: [[Backend/Files]] · Resumable upload: [[Backend/ResumableUpload]] · Identity: [[Backend/Identity]] · IPC и proto: [[Shared/Proto]]

## Назначение

Клиент Windows монтирует облачные каталоги как диск через Dokany. Приложение `BarkCloud.Drive.App` управляет скрытым процессом `BarkCloud.Drive.Engine` через контракты `BarkCloud.Drive.Contracts`.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Drive/BarkCloud.Drive.Contracts/IDriveEngine.cs` | `IDriveEngine` | Контракты IPC входа, управления диском, статуса, настроек и завершения Engine |
| `Drive/BarkCloud.Drive.Contracts/EngineStatus.cs` | `EngineStatus` | DTO состояния IPC; `PendingUploads` и `LastSyncError` — поля статуса |
| `Drive/BarkCloud.Drive.Contracts/WebAuthnChallenge.cs` | `WebAuthnChallenge` | Options, challenge ID, RP ID и ошибка passwordless-входа |
| `Drive/BarkCloud.Drive.Contracts/EnginePipe.cs` | `EnginePipe` | Имя pipe и общие параметры доступа |
| `Drive/BarkCloud.Drive.Contracts/ServerConfig.cs` | `ServerConfig` | Адреса self-hosted сервера и флаг проверки сертификата |
| `Drive/BarkCloud.Drive.Contracts/DriveStoragePaths.cs` | `DriveStoragePaths` | Общий путь outbox для App и Engine |
| `Drive/BarkCloud.Drive.Engine/Program.cs` | top-level statements, `EngineConfig` | Конфигурация, соединение, восстановление сессии и запуск IPC |
| `Drive/BarkCloud.Drive.Engine/EnginePipeServer.cs` | `EnginePipeServer` | Цикл подключения UI и завершение RPC |
| `Drive/BarkCloud.Drive.Engine/DriveEngine.cs` | `DriveEngine` | IPC, сессия, mount/logout/shutdown и worker outbox |
| `Drive/BarkCloud.Drive.Engine/UserProfile.cs` | `UserProfile` | Загрузка профиля и ID текущего пользователя |
| `Drive/BarkCloud.Drive.Engine/BarkCloudFileSystem.cs` | `BarkCloudFileSystem`, `LocalFile`, `PersistOperation` | Файловые операции, реестр, сохранение и квота диска |
| `Drive/BarkCloud.Drive.Engine/CloudGateway.cs` | `CloudGateway` | gRPC/HTTP, листинги, кеш и передача содержимого |
| `Drive/BarkCloud.Drive.Engine/BarkCloudConnection.cs` | `BarkCloudConnection` | Каналы Identity, Files/Cloud и Users, HTTP-клиент |
| `Drive/BarkCloud.Drive.Engine/ConnectionErrors.cs` | `ConnectionErrors` | Представление TLS- и RPC-ошибок для UI и `LastSyncError` |
| `Drive/BarkCloud.Drive.Engine/ServerErrors.cs` | `ServerErrors` | Коды ошибок сервера из trailer `x-error-code` gRPC |
| `Drive/BarkCloud.Drive.Engine/MountManager.cs` | `MountManager` | Монтирование и размонтирование Dokany |
| `Drive/BarkCloud.Drive.Engine/MetadataInterceptor.cs` | `MetadataInterceptor` | Device/auth metadata; unary-вызовы без своего deadline получают 30 с |
| `Drive/BarkCloud.Drive.Engine/DeviceIdentity.cs` | `DeviceIdentity` | Устойчивый ID устройства и device metadata |
| `Drive/BarkCloud.Drive.Engine/DokanKeepAlive.cs` | `DokanKeepAlive` | Продление таймаута долгого Dokany callback |
| `Drive/BarkCloud.Drive.Engine/ResumableUploader.cs` | `ResumableUploader` | Upload 2.0 для файлов от 64 МиБ |
| `Drive/BarkCloud.Drive.Engine/UploadOutbox.cs` | `UploadOutbox`, `UploadOutboxWorker` | Манифесты, данные и повторы отправки |
| `Drive/BarkCloud.Drive.Engine/TokenManager.cs`, `TokenStore.cs`, `EngineServerKey.cs` | `TokenManager`, `TokenStore`, `EngineServerKey` | Восстановление сессии и привязка refresh-токена к серверу |
| `Drive/BarkCloud.Drive.Engine/EngineSettingsStore.cs` | `EngineSettingsStore` | Настройки кеша, последнего пользователя и монтирования |
| `Drive/BarkCloud.Drive.Engine/EngineLog.cs` | `EngineLog` | Файл диагностики и ротация журнала |
| `Drive/BarkCloud.Drive.App/EngineLauncher.cs`, `MainWindow.xaml.cs` | `EngineLauncher`, `MainWindow` | Запуск, IPC-переподключение, перезапуск и UI |
| `Drive/BarkCloud.Drive.App/AppSettings.cs`, `SettingsWindow.xaml.cs` | `AppSettings`, `SettingsWindow` | Настройки приложения и экрана |
| `Drive/BarkCloud.Drive.App/WebAuthnClient.cs` | `WebAuthnClient` | Вызов Windows WebAuthn API для passwordless-входа |

## IPC и подключение

- Pipe имеет имя `BarkCloud.Drive.Engine.<SID>`; `EnginePipe` берёт SID из `WindowsIdentity.GetCurrent().User` и задаёт `PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`. `EnginePipeServer` принимает клиентов по одному и ждёт до 3 с завершения RPC после остановки.
- `IDriveEngine` предоставляет `LoginAsync`, `BeginWebAuthnAsync`/`CompleteWebAuthnAsync`, `LogoutAsync`, `MountAsync`/`RemountAsync`/`UnmountAsync`, `GetAvatarAsync`, `SetLanguageAsync` и `ShutdownAsync`; `PendingUploads` и `LastSyncError` — поля `EngineStatus`. Engine — singleton текущего пользователя и переживает отключение UI.
- `BeginWebAuthnAsync` возвращает `WebAuthnChallenge` с options, challenge ID и RP ID; `WebAuthnClient` вызывает Windows WebAuthn API. Кнопка мастера видна только на шаге входа, до аутентификации, при `WebAuthnClient.IsSupported` (Windows 10 1903+) и доменном имени сервера, не IP.
- `_sessionOperations` сериализует `LoginAsync`, `CompleteWebAuthnAsync`, `LogoutAsync` и операции mount. При смене сессии outbox останавливается, удаления отправляются и диск размонтируется со старым токеном до применения новых; после смены сессии, успешной или нет, диск монтируется обратно на прежнюю букву.
- `MountAsync` и `RemountAsync` восстанавливают прежнюю метку тома при ошибке. `RemountAsync` принимает `null`, чтобы оставить букву или метку текущими; `GetAvatarAsync` возвращает байты аватара либо `null`, а `SetLanguageAsync` задаёт язык последующих статусов и ошибок (`ru`, `en`, `de`).
- `Program.cs` создаёт отдельные gRPC-каналы Identity, Files/Cloud и Users. `MetadataInterceptor` добавляет device/auth metadata; `DeviceIdentity.AppVersion` берётся из `AssemblyInformationalVersion` без суффикса `+hash`; для unary-вызовов без заданного deadline применяется 30 с.
- Загрузка байтов Upload 2.0 идёт на `https://{Host}` по порту 443. Этот endpoint отдельный от gRPC `FilesPort` и `/web`; self-hosted сервер должен быть доступен клиенту на 443.
- `ServerConfig.AcceptAnyCert` и `DangerousAcceptAnyServerCert` выключены по умолчанию, поэтому TLS-проверка строгая. Галочка «Принимать любые сертификаты сервера» есть в мастере и настройках и по умолчанию выключена; включать её следует только для доверенного self-hosted-сервера. Явное `AcceptAnyCert: true` в существующем `server.json` сохраняет прежнее поведение.
- `ConnectionErrors` распознаёт `AuthenticationException`, в том числе во вложенных исключениях, и показывает TLS-подсказку проверить адрес и сертификат либо для доверенного self-hosted-сервера временно включить «Принимать любые сертификаты сервера»; при сбое `Persist` сообщение попадает в `LastSyncError`.

## Сессия и outbox

- `EngineServerKey.FromConfig` нормализует HTTPS Identity и включает порты Files/Users. `TokenStore` хранит refresh-токен в DPAPI с ключом сервера; при несовпадении токен очищается. Старый raw DPAPI refresh-токен без `ServerKey` при первом чтении привязывается к текущей конфигурации без проверки, кем он выдан.
- `TokenManager.SessionRestored` вызывает `DriveEngine.SessionRestoredAsync`: профиль загружается, запускается outbox worker и автомонтируется последний диск. Временный сбой восстановления refresh-токена повторяется с backoff от 5 с до 5 мин.
- Владелец outbox загружается через `UserProfile.EnsureLoadedAsync` (`UsersApi.GetUser(0).User.Id`); до загрузки профиля используется `EngineSettingsStore.GetLastUserId(serverKey)`. `TokenManager` отбрасывает поздние успехи и отказы `Auth`, `CreateToken` и `Refresh`, если поколение сессии уже изменилось.
- Outbox хранит запись для `ServerKey` и `OwnerUserId`. При `OwnerUserId=null` запись скрыта; первая аутентифицированная сессия того же сервера усыновляет её и сохраняет владельца в манифест.
- При logout worker останавливается, диск размонтируется до очистки токена, затем best-effort выполняется remote logout и очищаются кеш/профиль. Outbox остаётся на диске. Пустые заготовки при logout или смене аккаунта удаляются; заготовки с данными остаются на диске, а их путь фиксируется в `LastSyncError`.

## Файлы, кеш и сохранение

- `BarkCloudFileSystem` ведёт общие `LocalFile` для хэндлов одного пути. Outbox-файлы восстанавливаются как `Pending` по `(DirectoryId, Name)`; Pending доступен для чтения, но блокирует запись, удаление и перенос.
- Reader-хэндл хранит `ResolvedNode` со снимочным `EntryId`. После сохранения новой версии `DeleteOnClose` на старом хэндле ставит удаление старой записи; актуальная версия остаётся.
- `PersistOperation.Stage` проходит `Upload` (при ожидании сервера — `Processing`), `DeleteOld`, `Attach`, `Done`. При подтверждённой ошибке `Attach` после удаления старой записи она восстанавливается через `RestoreFromTrash`.
- При `StorageLimit == 0` (сервер отдаёт `LimitBytes ?? 0`) квоты нет: общий объём равен `TotalAvailableStorage`, занято — `DiskUsedStorage + S3UsedStorage`; при лимите общий объём равен `StorageLimit`, занято — `TotalUsedStorage + ReservedStorage`. Отрицательный остаток ограничивается нулём.
- `CloudGateway.ClearCache` увеличивает эпоху аккаунта и удаляет только свои артефакты кеша (`<guid>`, `<guid>.blocks`, `.part`); каталоги `write` и `outbox` исключены. HTTP Range-чтение использует блоки 1 МиБ; при неподдерживаемом Range `CloudGateway` скачивает файл целиком. Кеш листингов имеет TTL 5 с. Лимит дискового кеша — 5 ГиБ, очистка целится в 80%; write/outbox не вытесняются.
- `Cleanup` уменьшает `ActiveCount` и вызывает `Persist` для изменённого файла, когда активных хэндлов не осталось; `CloseFile` уменьшает `OpenCount` и сохраняет оставшиеся изменения при последнем закрытии. Paging-запись после `Cleanup` помечается `DirtyAfterPersist` и повторно сохраняется при `CloseFile`.
- `CloudGateway.QueueDeleteFileEntry` сразу скрывает запись тумбстоном и буферизует удаление пачкой; при ошибке отправки пачка возвращается в очередь с тумбстонами до успеха или исчерпания попыток. `MoveFile` синхронно вызывает `FlushPending(throwOnFailure: true)` при цели с тумбстоном и не перемещает источник, если удаление не доставлено.
- Upload 2.0 применяется от 64 МиБ: состояние resumable-сессии сохраняется, части возобновляются, а `PROCESSING` ждётся до 2 мин за проход и затем продолжается worker-ом. Повторный `Resume` ограничен тремя повторами для кодов ошибки сервера `UploadSessionNotFound`, `UploadTokenInvalid`, `UploadSessionState` из `ServerErrors`, читаемых из поля `code` JSON-тела ответа на PUT части; отмена отправляется best-effort только при `PersistStage.Upload`.
- `MoveFile` возвращает `AccessDenied` для `Pending`-источника или цели и `SharingViolation` для открытой цели; при одновременной смене папки и имени используется временное имя `.~bc-<8 hex>`. Выполненные шаги замены откатываются при сбое. Перед рекурсивным удалением папки файловая система проверяет локальные/outbox-записи и свежий листинг; остаётся гонка с другим клиентом после проверки.

## Жизненный цикл App

- `MainWindow` не допускает параллельных операций подключения/опроса. Потеря IPC освобождает proxy и инициирует переподключение через `EngineLauncher`; закрытие окна скрывает его в трей, где статус обновляется раз в 30 с.
- Перезапуск отправляет `ShutdownAsync`, ждёт до 60 с, затем может завершить только процесс текущей установки и сеанса и подключиться заново. Если закрыть App во время перезапуска, он завершается без отправки собственного `ShutdownAsync`; уже поднятый к этому моменту новый Engine продолжит работу без UI.
- В трее нет balloon API: подсказка показывает `LastSyncError`, иначе количество `PendingUploads`; пункт outbox доступен при наличии ожидающих файлов.
- `EngineSettingsStore`, `ServerConfig.Save` и `AppSettings.Save` сохраняют настройки во временный файл с заменой через `File.Move(overwrite: true)`. `EngineLog` ротирует `engine.log` в `engine.log.1` при размере свыше 5 МиБ.

## Ограничения

- Новые пустые файлы остаются runtime-заготовками и исчезают после перезапуска Engine; пустая правка существующего файла не загружается.
- Сохранение создаёт новый `fileId`, поэтому привязанные к старому ID ссылки и избранное не переносятся.
- Имена разрешаются без учёта регистра; совпадения, отличающиеся регистром, независимо не адресуются, побеждает первое в листинге.
- Кеш 5 ГиБ имеет мягкий лимит: кандидаты моложе минуты пропускаются, открытые файлы не удаляются из-за `IOException`, а LRU-touch выполняется примерно раз в 30 с отдельно от очистки раз в минуту.
- `UploadOutbox.Remove` удаляет манифест и `.bin` по отдельности; конкурентный reader может получить `FileNotFound` или оставить `.bin` без манифеста.
- Сервер не предоставляет атомарную замену содержимого; очередь удалений хранится в памяти и не переживает процесс. Имена, недопустимые для Windows, не преобразуются в отдельное отображаемое имя.
