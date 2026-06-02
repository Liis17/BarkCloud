# Аудит Windows-клиента BarkCloud.Drive — безопасность, производительность, качество кода

> Пошаговый план проверки десктопного клиента (виртуальный диск `X:` поверх облака на Dokany).
> Версия документа: 1.0 · Дата: 2026-06-02 · Охват: `Drive/` (Engine + App + Contracts).
> Парный документ для backend/web — `SECURITY_PERFORMANCE_AUDIT.md`. Контекст модуля — [[modules/windows-drive]].

## 0. Введение

### 0.1 Цель
Систематически и повторяемо проверить десктопный клиент BarkCloud.Drive на три класса проблем:
**безопасность** (TLS, хранение секретов, IPC, утечки данных на диск), **производительность**
(блокирующий I/O в колбэках Dokany, кэш, резолв путей, нагрузка) и **качество кода** (проглоченные
исключения, мёртвый код, отсутствие тестов). Документ — методология «по шагам» с фиксацией находок
в едином формате (раздел 6) и приложением известных горячих точек по `file:line` (раздел 7).

### 0.2 Охват
- **В охвате (`Drive/`):**
  - **BarkCloud.Drive.Engine** — скрытый `WinExe`: gRPC-каналы, `TokenManager`/`TokenStore` (DPAPI),
    `MetadataInterceptor`, `CloudGateway` (кэш/чтение/запись), `BarkCloudFileSystem` (Dokany),
    `MountManager`, IPC-сервер (named pipe + StreamJsonRpc), `Program`.
  - **BarkCloud.Drive.App** — WPF-UI + трей: `EngineLauncher` (IPC-клиент), `Autostart` (HKCU\Run),
    `ServerConfig`/`AppSettings`/`EngineSettingsStore`, окна (`FirstRunWizard`/`MainWindow`/`SettingsWindow`).
  - **BarkCloud.Drive.Contracts** — IPC-контракт `IDriveEngine`, DTO, `ServerConfig`.
- **Вне охвата:** backend и web (см. парный документ), Android/iOS, сам драйвер Dokany 2.x и
  инсталлятор (пока не реализован). Корректность серверного Range/дедупа — лишь как граничное условие.

### 0.3 Доверительные границы и модель угроз
- **UI ↔ Движок:** локальный **named pipe** (`BarkCloud.Drive.Engine`) + StreamJsonRpc. По нему UI
  передаёт **логин/пароль в открытом виде** (`LoginAsync`) и команды управления (`MountAsync`,
  `SetCacheDirAsync`, `ShutdownAsync`). Граница: другие процессы того же пользователя / процессы с
  иным уровнем целостности на той же машине.
- **Движок ↔ backend:** gRPC поверх TLS (nginx, порты 7020/7021/7025) + HTTP для байтов
  (`/upload`, `/download`, порт Files). Граница: сеть между клиентом и сервером (MITM).
- **Секреты на диске:** `%LOCALAPPDATA%\BarkCloud.Drive\` — `refresh.bin` (DPAPI), `device-id`,
  `server.json`, `settings.json`, `app.json`, `engine.log`. Граница: другой пользователь Windows,
  вредонос в профиле пользователя, бэкап/синхронизация профиля.
- **Кэш содержимого:** расшифрованные байты облака и рабочие копии записи в `%TEMP%\BarkCloudDrive`
  (по умолчанию). Граница: остаточные данные, доступ других процессов к `%TEMP%`.
- **Диск `X:`:** read-write проекция облака в Проводник. Граница: любой процесс пользователя видит
  смонтированный диск как обычную ФС.

### 0.4 Легенда severity
**Critical** — удалённый перехват учётных данных/токенов/контента или исполнение под пользователем без
сложных условий. **High** — реалистичный MITM/локальная эскалация, отказ движка/диска (deadlock, hang).
**Medium** — утечка данных на диск, отсутствие очистки, деградация под нагрузкой. **Low** —
hardening, мёртвый код, диагностические дефекты.

---

## 1. Этап 0 — Подготовка окружения и инструментов

1. Собрать решение: `Drive/BarkCloud.Drive.slnx` (.NET, два процесса). Зафиксировать версии:
   .NET TFM, `DokanNet`, `Grpc.Net.Client`, `StreamJsonRpc`, `WPF-UI`, версия драйвера Dokany 2.x.
2. Поднять backend (dev-стенд из парного документа) + тестовый аккаунт; задать адрес сервера в мастере.
3. Инструменты:
   - **MITM/TLS:** Burp/mitmproxy + поднять прокси и проверить, **принимает ли клиент подменённый
     сертификат** (ожидаемо — да, см. раздел 3.1). Wireshark — подтвердить, что внутренний h2c/HTTP
     не утекает в открытом виде вне TLS.
   - **IPC:** Sysinternals **PipeList**/**Process Explorer** — посмотреть ACL named pipe
     `BarkCloud.Drive.Engine`; написать сторонний мини-клиент, который коннектится к pipe из другого
     процесса того же пользователя и зовёт `GetSettingsAsync`/`ShutdownAsync` (проверка авторизации вызова).
   - **Профилирование:** `dotnet-counters` (ThreadPool queue length, ThreadPool thread count),
     `dotnet-trace`, `dotnet-stack` — ловить блокировку пула из колбэков Dokany.
   - **ФС/диск:** Sysinternals **ProcMon** (файловые операции в `%TEMP%`/кэше), наблюдение за ростом
     кэша; нагрузка — массовое копирование/удаление/параллельное чтение больших файлов в `X:`.
   - **Секреты:** `gitleaks`/`trufflehog` по истории `Drive/`; ручной осмотр `appsettings.json`,
     `*.json` в `%LOCALAPPDATA%`, `engine.log` на предмет токенов/паролей.
4. Завести журнал находок по шаблону раздела 6.

> **Примечание:** в репозитории нет тестов под `Drive/` (каталог `Tests/` покрывает backend) — это
> отдельная находка (раздел 5). Большая часть проверок выполняется статически по якорям раздела 7 и
> динамически (MITM, сторонний pipe-клиент, нагрузка на `X:`).

---

## 2. Сквозные этапы аудита (W1–W8)

Методология, применяемая к компонентам раздела 3.

- **W1. Инвентаризация и threat model.** Карта процессов, каналов (pipe/gRPC/HTTP), файлов-секретов,
  точек входа IPC (`IDriveEngine`), колбэков Dokany. DFD от логина до байтов файла.
- **W2. Транспорт и TLS.** Валидация серверного сертификата на gRPC-каналах **и** на HTTP-транспорте
  байтов; дефолты `AcceptAnyCert`/`DangerousAcceptAnyServerCert`; пиннинг; downgrade.
- **W3. Секреты и хранение.** refresh-токен (DPAPI scope), access-токен (только в памяти), device-id,
  `server.json`; что и как пишется в `engine.log`; права на файлы в `%LOCALAPPDATA%`.
- **W4. IPC и локальная поверхность атаки.** ACL named pipe, аутентификация вызывающего, передача
  логина/пароля по pipe, идемпотентность/защита команд (`Shutdown`, `SetCacheDir`, `Mount`),
  single-instance (Mutex), `Process.Start`/`Kill` движка, разрешение пути к exe.
- **W5. Данные на диске (кэш и рабочие копии).** Где лежат расшифрованные байты, ACL папок,
  очистка `.part`/блоков/whole-файлов/`write`-копий, остаток после logout/unmount/crash, рост без
  ограничения.
- **W6. Корректность ФС и согласованность.** Семантика Dokany-колбэков (Cleanup vs WriteFile,
  DeletePending), конкурентность (колбэки многопоточны), кэш листинга и «поколения», тумбстоны
  удаления, обработка ошибок синхронизации (`LastSyncError`).
- **W7. Производительность.** Блокирующий `GetAwaiter().GetResult()` в колбэках, стоимость `Resolve`
  (RPC-цепочка на путь), TTL-кэши, поблочное чтение, батч-удаление, таймауты, нагрузка.
- **W8. Качество кода и диагностика.** Проглоченные `catch {}`, мёртвый код, магические числа,
  хардкод версии/адресов, логирование, **отсутствие тестов**, сборка без предупреждений.

---

## 3. Аудит по компонентам (по шагам)

Формат: **Security/Корректность (шаги)** → **Anchors** (где смотреть). Severity — предварительная,
подтверждается при выполнении. Производительность вынесена в раздел 4, качество кода — в раздел 5.

### 3.1 Транспорт и TLS — `BarkCloudConnection`, `ServerConfig`, `EngineConfig`
Этапы: W1, W2.

1. **[Critical] Полное отключение валидации сертификата по умолчанию.** Проверить, что при
   `acceptAnyCert == true` `RemoteCertificateValidationCallback` возвращает `true` для **всех** gRPC-каналов
   (Identity/Files/Users) **и** для HTTP-транспорта байтов. Дефолты `ServerConfig.AcceptAnyCert = true`,
   `EngineConfig.DangerousAcceptAnyServerCert = true` и дефолтный продакшн-хост `cloud.barkfluff.com`
   означают, что «из коробки» клиент уязвим к MITM: перехват логина/пароля (`Identity.Auth`), access/refresh
   токенов и содержимого файлов. Динамически подтвердить через прокси с подменным сертификатом.
   - Рекомендация к проверке: дефолт `AcceptAnyCert=false`; «принимать любой сертификат» — только явный
     opt-in для self-hosted в мастере, с предупреждением; рассмотреть пиннинг/доверие к конкретному CA.
2. **[Medium] Downgrade/схема адреса.** Адреса строятся как `https://{host}:{port}` (`Program.cs`) — убедиться,
   что нет пути, собирающего `http://`; что порт/хост из `server.json` валидируются (`ServerInput`).
3. **[Low] Единый `acceptAnyCert` на оба транспорта.** Один флаг управляет и gRPC, и bulk-HTTP — это ок,
   но зафиксировать, что нет второго «тихого» места создания `HttpClient`/канала без флага.

**Anchors:** `Engine/BarkCloudConnection.cs:42-43`, `:48-56` (gRPC-канал), `:41-45` (HTTP-транспорт);
`Contracts/ServerConfig.cs:16`; `Engine/Program.cs:20-22,113,130`.

### 3.2 Токены и сессия — `TokenManager`, `TokenStore`
Этапы: W1, W3.

1. **[ок→подтвердить] refresh в DPAPI, access только в памяти.** `TokenStore` шифрует refresh через
   `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` — Windows-аналог Keychain; access-токен
   не персистится. Подтвердить, что access никуда не пишется (лог/файл) и что `Logout()` чистит и память,
   и `refresh.bin`, и останавливает refresh-loop.
2. **[Low] optionalEntropy = null.** DPAPI без доп. энтропии — любой код в сессии пользователя расшифрует
   `refresh.bin`. Оценить добавление статической/машинной энтропии (защита глубины, не панацея).
3. **[Medium] Гонки refresh-loop.** Один `_refreshCts`, перезапуск при `Login`/`TryRestore`. Проверить, что
   нет двух параллельных циклов после `TryRestoreAsync` + `LoginAsync`; что при сбое refresh access
   обнуляется атомарно (`lock`), и интерсептор перестаёт слать мёртвый токен.
4. **[Low] Запас обновления 60 c, fallback exp = +5 мин.** Если сервер не отдал `ExpirationDate`, берётся
   +5 мин — проверить, что это не приводит к busy-loop рефреша при коротком/нулевом сроке.

**Anchors:** `Engine/TokenStore.cs:22-27,29-44`; `Engine/TokenManager.cs:35-44,47-59,62-84,94-139`.

### 3.3 Заголовки и device-identity — `MetadataInterceptor`, `DeviceIdentity`
Этапы: W2, W3.

1. **[ок→подтвердить] токен в метадате на каждый вызов.** `x-auth-token` — сырой, device-заголовки —
   Base64(UTF8). Подтвердить, что токен берётся динамически (`tokenProvider`) и не кэшируется в канале.
2. **[Low] device-id в открытом файле.** `%LOCALAPPDATA%\BarkCloud.Drive\device-id` — не секрет, но
   зафиксировать, что его подмена не даёт обхода (привязка refresh к device_id — серверная инвариант).
3. **[Low] Хардкод `AppVersion = "0.1.0"`.** Версия зашита в код — мелочь, но влияет на диагностику/серверные
   проверки `x-app-version`.

**Anchors:** `Engine/MetadataInterceptor.cs:30-44`; `Engine/DeviceIdentity.cs:15-22,24-38`.

### 3.4 IPC: named pipe + StreamJsonRpc — `Program`, `EngineLauncher`, `IDriveEngine`
Этапы: W1, W4. **Ключевой раздел локальной поверхности атаки.**

1. **[High] ACL named pipe не задан явно.** `NamedPipeServerStream(PipeName, …, maxInstances:1, …)` создаётся
   без `PipeSecurity`. Проверить фактический DACL pipe (`PipeList`/Process Explorer): кто может коннектиться.
   По named-pipe доходит **логин/пароль в открытом виде** (`LoginAsync`) и команды управления. Если pipe
   доступен другим процессам/уровням целостности — это перехват кредов и подмена команд. Зафиксировать,
   нужен ли явный `PipeSecurity` (только владелец сессии) и проверка peer'а.
2. **[High] Команды IPC без авторизации вызывающего.** Любой, кто подключился к pipe, может вызвать
   `ShutdownAsync` (убить движок/размонтировать), `SetCacheDirAsync` (увести кэш в произвольный путь —
   запись расшифрованных байтов куда угодно), `MountAsync`/`UnmountAsync`, `LogoutAsync`. Оценить
   необходимость аутентификации/ограничения команд.
3. **[Medium] `SetCacheDirAsync` — произвольный путь.** Валидация только на пустую строку (`DriveEngine.cs:171-180`).
   Путь не нормализуется/не проверяется (например, сетевой UNC, системная папка). Расшифрованный контент
   может писаться в неожиданное место.
4. **[Low] single-instance и обслуживание по одному клиенту.** Движок — `Mutex`; pipe обслуживает одного
   клиента (`maxInstances:1`). Проверить, что второй UI-процесс не виснет/корректно переиспользует, и что
   `ShutdownAsync` действительно завершает цикл.
5. **[Low] Запуск/убийство движка из App.** `EngineLauncher.StartEngine` (`Process.Start`, `UseShellExecute=false`)
   и `KillEngine` (`Process.GetProcessesByName("BarkCloud.Drive.Engine").Kill()`) — проверить разрешение пути
   к exe (`ResolveEnginePath`, dev-эвристика `Replace("App","Engine")`) на предмет запуска не того бинаря и
   убийства чужого одноимённого процесса.

**Anchors:** `Engine/Program.cs:71-91`; `App/EngineLauncher.cs:36-49,52-64,66-90`;
`Contracts/IDriveEngine.cs` (вся поверхность); `Engine/DriveEngine.cs:171-188`.

### 3.5 Данные на диске: кэш и рабочие копии — `CloudGateway`, `BarkCloudFileSystem`, `EngineSettingsStore`
Этапы: W5.

1. **[Medium] Расшифрованный контент в `%TEMP%` без очистки.** По умолчанию кэш — `%TEMP%\BarkCloudDrive`,
   рабочие копии записи — `%TEMP%\BarkCloudDrive\write`. Блоки (`{fileId}.blocks/{N}.blk`), whole-файлы
   (`{fileId}`) и `.part` накапливаются и **не вычищаются** (нет эвикции/лимита/очистки при logout/unmount).
   Оценить: ACL папок, остаток после выхода, политику TTL/размера, очистку на старте.
2. **[Medium] Смена кэша «на лету» оставляет старые байты.** `SetCacheDir` лишь чистит `_downloads` и меняет путь;
   ранее скачанное остаётся в прежней папке. Зафиксировать как утечку остаточных данных.
3. **[Low] Рабочая копия записи живёт до `Cleanup`/`CloseFile`.** `WriteSession.Dispose` удаляет `TempPath` —
   проверить, что при падении движка во время записи temp-файлы подчищаются (на старте?).

**Anchors:** `Engine/CloudGateway.cs:77-78,84-89,313-322,337-338,358-368`;
`Engine/BarkCloudFileSystem.cs:53-56,434-440,29-33`; `Engine/EngineSettingsStore.cs`.

### 3.6 Корректность ФС и согласованность — `BarkCloudFileSystem`, `CloudGateway`
Этапы: W6.

1. **[Medium] Конкурентность колбэков Dokany.** Колбэки многопоточны. Проверить потокобезопасность
   `WriteSession` (`Sync`-lock на Stream — есть), кэша листинга («поколения» `_listGen`+`_listLock`),
   тумбстонов удаления, `_blockFetches`/`_downloads` (`Lazy` + `ConcurrentDictionary`).
2. **[Medium] Семантика удаления через батч + тумбстоны.** Воспроизвести: массовое удаление, размонтаж/logout
   в окне тишины (1 c) — убедиться, что `FlushPending` дренирует всё (пути выхода `Logout/Unmount/Shutdown/
   DeleteDirectory/Dispose`), что при сбое сервера файл возвращается в листинг (`DropOne`), и нет «возврата»
   удалённого файла из устаревшего кэша.
3. **[Low] Ошибки синхронизации не глушатся.** `Cleanup` при сбое выставляет `LastSyncError` → статус.
   Подтвердить, что ошибка действительно доходит до UI и сбрасывается при следующей успешной записи.
4. **[Low] `DeleteFile` всегда `Success`, реальное удаление в `Cleanup`.** Документированный контракт Dokany —
   проверить, что нет сценария «нельзя удалить», который молча проглатывается.

**Anchors:** `Engine/BarkCloudFileSystem.cs:143-160,201-215,461-484,486-506`;
`Engine/CloudGateway.cs:104-145,450-466,480-543`.

### 3.7 App: настройки, автозагрузка, окна — `ServerConfig`, `AppSettings`, `Autostart`, окна
Этапы: W1, W3, W4.

1. **[Low] Автозагрузка через HKCU\Run.** Записи App (`--tray`) и Engine с полным путём в кавычках. Проверить
   корректность кавычек (пробелы в пути), что отключение удаляет значение, и что путь берётся из доверенного
   источника (`Environment.ProcessPath`/`EngineLauncher.EnginePath`).
2. **[Low] `server.json`/`app.json` без защиты.** Не секреты, но проверить, что повреждённый/подменённый
   `server.json` не уводит клиент на чужой хост незаметно (он перекрывает дефолты на старте движка).
3. **[Low] Логин/пароль в UI.** Пароль не должен задерживаться в полях/логах App после передачи в движок.

**Anchors:** `App/Autostart.cs:16-28,36-47`; `Contracts/ServerConfig.cs:23-46`; `App/AppSettings.cs`;
`App/FirstRunWizard.xaml.cs`, `App/SettingsWindow.xaml.cs`, `App/ServerInput.cs`.

---

## 4. Производительность (шаги)
Этап W7. Главный риск — **блокирующий I/O в синхронных колбэках Dokany**.

1. **[High] Sync-over-async в колбэках.** Колбэки Dokany синхронны и выполняются на пуле потоков; внутри —
   `GetAwaiter().GetResult()` на сетевых операциях. При параллельном I/O (копирование папки, несколько
   читателей больших файлов) это **истощает ThreadPool** и грозит залипанием диска/deadlock'ом. Нагрузить
   `X:` и снять `dotnet-counters` (ThreadPool Queue Length). Места:
   - `CloudGateway.EnsureBlock` → `lazy.Value.GetAwaiter().GetResult()` (блокирует поток на Range-GET);
   - `CloudGateway.ReadWhole` → `.Value.GetAwaiter().GetResult()` (гидрация целиком);
   - `BarkCloudFileSystem.OpenExistingForWrite` → `DownloadToAsync(...).GetAwaiter().GetResult()`;
   - `BarkCloudFileSystem.PersistSession` → `UploadAsync(...).GetAwaiter().GetResult()`.
   Оценить ограничение параллелизма и/или переход на корректную блокировку без захвата пула.
2. **[Medium] Стоимость `Resolve` — RPC-цепочка на путь.** Каждый `CreateFile`/`GetFileInformation`/`ReadFile`
   резолвит путь посегментно через `ListDirectory` (TTL-кэш 5 c). Проводник шлёт пачки метаданных-колбэков →
   всплески RPC при промахах кэша. Замерить число `ListDirectoryDetailed` на типовые операции; рассмотреть
   обслуживание `GetFileInformation` из кэша листинга родителя.
3. **[Medium] Кэш без эвикции (см. 3.5).** Помимо приватности — неограниченный рост диска и замедление
   (тысячи `.blk` в одной папке). Нужна политика размера/TTL.
4. **[Low] Накопление `_downloads`/`_wholeMode`/`_tempUrls`.** Записи `_downloads` (whole-режим) и `_wholeMode`
   не удаляются — рост памяти при долгой сессии с многими файлами. `_tempUrls` TTL 50 мин, но без удаления
   просроченных. Оценить очистку.
5. **[Low] `HidePending` копирует листинг и сканирует на каждый чтении при наличии тумбстонов.** LINQ `.Any` +
   копия `DirectoryListingDetailed` — на горячем пути листинга. Незначительно, но отметить.
6. **[ок] Батч-удаление, поблочное чтение, TTL-кэши storage/listing** — спроектированы разумно; задача аудита —
   подтвердить пороги (1 c / 100 / дедлайн 5 c) и отсутствие лишних RPC.
7. **[Medium] `HttpClient.Timeout = InfiniteTimeSpan`.** Сознательно (крупные передачи), но без таймаута зависший
   сервер/сеть навечно держит поток-колбэк. Рассмотреть таймаут на операцию/`CancellationToken` вместо
   бесконечного глобального.

**Нагрузочные сценарии:** (а) рекурсивное копирование большого дерева В `X:` и ИЗ `X:`; (б) 5–10 параллельных
читателей файлов >100 МБ; (в) массовое удаление 1000+ файлов; (г) правка большого файла (гидрация+перезалив).
Метрики: latency колбэков, ThreadPool queue/threads, рост кэша, число RPC, отсутствие зависаний/`LastSyncError`.

---

## 5. Качество кода (шаги)
Этап W8.

1. **[Medium] Отсутствие тестов под `Drive/`.** Нет юнит/интеграционных тестов на резолв путей, батч-удаление,
   поблочное чтение, восстановление сессии, кэш-поколения. Спроектировать минимальный набор (резолв,
   `SendChunks`/`RequeueFailed`, `HidePending`/тумбстоны) — это самая хрупкая логика.
2. **[Low] Широкие `catch {}`.** `TokenStore` (повреждён/чужой профиль), `ServerConfig.Load`, `SafeResolve`,
   `DeleteDirectory`-листинг, `DownloadAvatarAsync` — часть оправдана, но провести ревизию: не маскируется ли
   реальная ошибка (например, проглоченный сбой логирования/синхронизации).
3. **[Low] Мёртвый/неактуальный код.** `Console.WriteLine` в `WinExe` без консоли (`Program.cs:65,94`) — уходит
   в никуда (диагностика — через `EngineLog`). Хардкод `AppVersion="0.1.0"`. Проверить на предупреждения сборки.
4. **[Low] Логирование.** Убедиться, что `EngineLog` (включая `RpcException.Detail`) **никогда** не пишет токен/
   пароль; что `engine.log` не растёт без ротации; что путь лога — только профиль пользователя.
5. **[Low] Магические числа.** `BlockSize`, TTL, пороги батча — вынесены константами (ок), но рассмотреть
   конфигурируемость кэша/таймаутов.

**Anchors:** `Engine/EngineLog.cs`; `Engine/Program.cs:65,94`; `Engine/DeviceIdentity.cs:20`;
`Engine/TokenStore.cs:40-43,53-56`; `Contracts/ServerConfig.cs:33-37`; `Engine/BarkCloudFileSystem.cs:370,508-512`.

---

## 6. Шаблон фиксации находок

```
### [W-NN] <Краткое название>
- Severity: Critical | High | Medium | Low
- Этап: W1..W8 / раздел
- Компонент: <проект/файл:line>
- Описание: <что не так, почему это проблема в данной модели угроз>
- Воспроизведение/доказательство: <шаги, MITM-лог, dotnet-counters, ProcMon, код>
- Влияние: <конфиденциальность / целостность / доступность / производительность / поддерживаемость>
- Рекомендация: <минимальная правка; ссылка на правило простоты из CLAUDE.md>
- Статус: Open | Confirmed | Fixed | Wontfix
```

Итоговую сводку оформить как `WINDOWS_DRIVE_AUDIT_FINDINGS.md` (по образцу `SECURITY_AUDIT_FINDINGS.md`):
матрица severity, верификация по коду, приоритеты ремедиации.

---

## 7. Приложение — предварительные горячие точки (file:line)

> Оценки предварительные (по результатам разведки кода), подтверждаются при выполнении этапов.

| Severity | Где | Что |
|---|---|---|
| **Critical** | `Engine/BarkCloudConnection.cs:42-43,51-52` + `Contracts/ServerConfig.cs:16` + `Engine/Program.cs:130` | Валидация TLS-сертификата отключена по умолчанию (gRPC + HTTP) → MITM на креды/токены/контент |
| **High** | `Engine/Program.cs:71-91` | Named pipe без явного ACL; по нему идут логин/пароль и команды управления |
| **High** | `Contracts/IDriveEngine.cs` + `Engine/DriveEngine.cs:171-188` | IPC-команды (`Shutdown`/`SetCacheDir`/`Mount`) без авторизации вызывающего |
| **High** | `Engine/CloudGateway.cs:292,345`; `Engine/BarkCloudFileSystem.cs:446,466` | `GetAwaiter().GetResult()` в синхронных колбэках Dokany → риск истощения ThreadPool/deadlock |
| **Medium** | `Engine/BarkCloudFileSystem.cs:54`; `Engine/CloudGateway.cs:77-78,313-322,358-368` | Расшифрованный контент в `%TEMP%` без эвикции/очистки |
| **Medium** | `Engine/DriveEngine.cs:171-180` | `SetCacheDirAsync` принимает произвольный путь (нет нормализации/проверки) |
| **Medium** | `Engine/BarkCloudConnection.cs:45` | `HttpClient.Timeout = InfiniteTimeSpan` — зависший сервер держит поток вечно |
| **Medium** | `Engine/CloudGateway.cs:147-201` | `Resolve` — RPC-цепочка на каждый путь/колбэк (TTL-кэш 5 c смягчает) |
| **Medium** | `Drive/` (нет `Tests/`) | Отсутствуют тесты на самую хрупкую логику (резолв, батч-удаление, кэш-поколения) |
| **Low** | `Engine/TokenStore.cs:24-25` | DPAPI без `optionalEntropy` |
| **Low** | `Engine/CloudGateway.cs:343-344,53,51` | `_downloads`/`_wholeMode`/`_tempUrls` без удаления записей |
| **Low** | `Engine/Program.cs:65,94`; `Engine/DeviceIdentity.cs:20` | `Console.WriteLine` в `WinExe`; хардкод версии |
| **Low** | `App/EngineLauncher.cs:78-90` | Эвристика разрешения пути к exe движка / `KillEngine` по имени процесса |

---

## 8. Порядок выполнения (рекомендуемый)

1. **W2/3.1 (TLS)** — быстрый MITM-тест: подтвердить Critical, это блокирующая находка для любого недоверенного канала.
2. **W4/3.4 (IPC)** — осмотр ACL pipe + сторонний клиент: подтвердить High по локальной поверхности.
3. **W7/раздел 4 (производительность)** — нагрузка на `X:` + `dotnet-counters`: подтвердить риск deadlock'а.
4. **W5/3.5 (данные на диске)** — ProcMon + осмотр `%TEMP%`/`%LOCALAPPDATA%`.
5. **W6 (корректность)** — сценарии удаления/записи/конкурентности.
6. **W8/раздел 5 (качество)** — статически, параллельно.
7. Свести находки в `WINDOWS_DRIVE_AUDIT_FINDINGS.md`, приоритизировать ремедиацию (Critical/High → дефолты TLS,
   ACL pipe, ограничение параллелизма колбэков).
