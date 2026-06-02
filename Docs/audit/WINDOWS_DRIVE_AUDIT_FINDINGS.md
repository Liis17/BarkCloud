# Отчёт по аудиту Windows-клиента BarkCloud.Drive

> Выполнено по плану `Docs/audit/WINDOWS_DRIVE_AUDIT.md`. Дата: 2026-06-02.
> Метод: **статическая верификация по исходному коду** (чтение фактических файлов `Drive/`),
> компонент за компонентом. Каждая находка имеет **статус**: ✅ подтверждено по коду ·
> ✏️ скорректировано относительно предварительной формулировки · ⏳ требует прогона на Windows.

## Ограничение окружения

Аудит проводился в Linux-контейнере без Windows, драйвера Dokany, запущенного backend и GUI.
Поэтому **динамические** проверки из плана выполнены НЕ были и помечены ⏳:
MITM через прокси с подменным сертификатом, `dotnet-counters`/`dotnet-trace` под нагрузкой на `X:`,
ProcMon по `%TEMP%`, осмотр DACL named pipe через PipeList, сторонний pipe-клиент, сборка
(`net10.0-windows` + WPF не собирается на Linux). Все находки ниже опираются на чтение кода;
там, где итоговое подтверждение требует прогона, это явно указано.

## Резюме

Проверены все три проекта: `BarkCloud.Drive.Engine`, `BarkCloud.Drive.App`, `BarkCloud.Drive.Contracts`
(25 файлов, ~3.5k строк C#).

| Severity | Кол-во | Находки |
|----------|--------|---------|
| Critical | 1 | C1 валидация TLS-сертификата отключена по умолчанию (gRPC + HTTP) → MITM на креды/токены/контент |
| High | 2 | H1 IPC named pipe: команды управления без аутентификации вызывающего · H2 блокирующий `GetAwaiter().GetResult()` в синхронных колбэках Dokany → риск deadlock под конкурентным I/O |
| Medium | 4 | M1 расшифрованный контент в `%TEMP%` без эвикции/очистки · M2 `SetCacheDirAsync` принимает произвольный путь · M3 `HttpClient.Timeout=Infinite` · M4 `Resolve` — RPC-цепочка на каждый путь/колбэк |
| Low | 5 | L1 DPAPI без `optionalEntropy` · L2 несбрасываемые in-memory словари + лог без ротации · L3 `Console.WriteLine` в `WinExe` + хардкод версии · L4 `KillEngine` по имени процесса / dev-эвристика пути · L5 нет тестов на хрупкую логику |

**Ключевая цепочка атаки (C1):** «из коробки» `AcceptAnyCert=true` по всем каналам → активный
сетевой посредник между клиентом и сервером подменяет TLS-сертификат → перехватывает `Identity.Auth`
(логин+пароль), access/refresh-токены и содержимое файлов в обе стороны (upload/download). Не требует
доступа к машине пользователя — достаточно позиции MITM (публичный Wi-Fi, скомпрометированный роутер).
Это тот же класс, что C2 backend-отчёта (SMTP без проверки сертификата), но на клиентском транспорте.

---

## Поправки к предварительным находкам (из плана)

Три предварительные формулировки плана при проверке по коду уточнены:

| Предв. формулировка (план) | Реальность (проверено) | Итог |
|---|---|---|
| Named pipe «доступен любому локальному пользователю/процессу» | `NamedPipeServerStream` без явного `PipeSecurity` → дефолтный DACL ограничивает доступ **владельцем/сессией** (кросс-юзер блокируется ОС). Перехват пароля ограничен гонкой (pipe занят, пока UI подключён, `maxInstances:1`); **команды управления** доступны процессу того же пользователя в окне, когда UI отключён (UI можно закрыть, движок живёт) | ✏️ сужено, остаётся **High**; точный DACL — ⏳ подтвердить PipeList |
| «ThreadPool starvation» в колбэках | Колбэки Dokany исполняются на **нативных потоках Dokan**, а не на .NET ThreadPool; блокировка `.GetResult()` держит поток Dokan, а continuation'ы async-операций нуждаются в ThreadPool → классический deadlock возможен под конкуренцией/насыщении пула | ✏️ механизм уточнён (H2); итоговое подтверждение — ⏳ нагрузка |
| Логирование может «писать токен/пароль» (план §5.4) | Проверены все вызовы `EngineLog.Info/Error`: пишутся имя файла, `fileId`, `entryId`, путь кэша, адрес сервера и `RpcException.Detail` (серверный текст ошибки). **Токен/пароль не логируются нигде** | ❌ опровергнуто; остаётся лишь отсутствие ротации лога (L2) |

---

## Сводная таблица находок

| ID | Severity | Тип | Находка | Файл:строка | Статус |
|----|----------|-----|---------|-------------|--------|
| C1 | Critical | Sec | Валидация TLS-сертификата отключена по умолчанию для всех gRPC-каналов и HTTP-транспорта байтов | `Engine/BarkCloudConnection.cs:42-43,51-52`, `Contracts/ServerConfig.cs:16`, `Engine/Program.cs:130`, `Engine/appsettings.json:6` | ✅ |
| H1 | High | Sec | IPC: команды `Shutdown`/`SetCacheDir`/`Mount`/`Logout` без аутентификации вызывающего; пароль идёт по pipe открытым текстом | `Engine/Program.cs:71-91`, `Contracts/IDriveEngine.cs`, `Engine/DriveEngine.cs` | ✅ / DACL ⏳ |
| H2 | High | Perf | `GetAwaiter().GetResult()` в синхронных колбэках Dokany → риск deadlock/залипания диска под конкурентным I/O | `Engine/CloudGateway.cs:292,345`, `Engine/BarkCloudFileSystem.cs:446,466` | ✅ / ⏳ нагрузка |
| M1 | Medium | Sec | Расшифрованный контент облака в `%TEMP%\BarkCloudDrive` без эвикции/очистки/лимита | `Engine/EngineSettingsStore.cs:21`, `Engine/BarkCloudFileSystem.cs:54`, `Engine/CloudGateway.cs:313-322,358-368` | ✅ |
| M2 | Medium | Sec | `SetCacheDirAsync` принимает произвольный путь (проверка только на пустоту) | `Engine/DriveEngine.cs:171-180` | ✅ |
| M3 | Medium | Perf | `HttpClient.Timeout = InfiniteTimeSpan` — зависший сервер держит поток-колбэк бесконечно | `Engine/BarkCloudConnection.cs:45` | ✅ |
| M4 | Medium | Perf | `Resolve` резолвит путь посегментно RPC-вызовами на каждый колбэк (TTL-кэш 5 c смягчает) | `Engine/CloudGateway.cs:147-201`, `Engine/BarkCloudFileSystem.cs:224-249` | ✅ |
| L1 | Low | Sec | DPAPI шифрует refresh без `optionalEntropy` (любой код в сессии расшифрует) | `Engine/TokenStore.cs:24-25` | ✅ |
| L2 | Low | Perf/Qual | `_downloads`/`_wholeMode`/`_tempUrls` без удаления записей; `engine.log` без ротации | `Engine/CloudGateway.cs:51,53,343-344`, `Engine/EngineLog.cs:41` | ✅ |
| L3 | Low | Qual | `Console.WriteLine` в `WinExe` без консоли; хардкод `AppVersion="0.1.0"` | `Engine/Program.cs:65,94`, `Engine/DeviceIdentity.cs:20` | ✅ |
| L4 | Low | Qual | `KillEngine` бьёт по имени процесса; dev-эвристика пути к exe через `Replace` | `App/EngineLauncher.cs:52-64,78-90` | ✅ |
| L5 | Low | Qual | Нет тестов под `Drive/` на самую хрупкую логику (резолв, батч-удаление, кэш-поколения) | каталог `Tests/` (только Backend/Shared) | ✅ |

---

## Детали находок

### C1 — TLS-валидация отключена по умолчанию (Critical) ✅
**Где:** `Engine/BarkCloudConnection.cs:42-43` (HTTP-транспорт) и `:51-52` (gRPC-каналы) —
`RemoteCertificateValidationCallback = (_, _, _, _) => true`. Включается флагом `acceptAnyCert`,
который по умолчанию `true` в трёх местах: `Contracts/ServerConfig.cs:16` (`AcceptAnyCert = true`),
`Engine/Program.cs:130` (`DangerousAcceptAnyServerCert = true`), `Engine/appsettings.json:6`.
Дефолтный хост — продакшн `cloud.barkfluff.com` (`Program.cs:126`).

**Почему проблема:** при первом запуске (до того как пользователь что-то настроит) и при отсутствии
`server.json` клиент принимает **любой** сертификат на всех трёх gRPC-каналах (Identity/Files/Users)
и на HTTP-канале байтов. Активный MITM перехватывает: пароль в `Identity.Auth`, access- и
refresh-токены, тело каждого upload/download. Это компрометация и аутентификации, и
конфиденциальности данных. Единый источник (один флаг на все каналы) — это плюс для ремедиации:
других «тихих» мест создания канала/`HttpClient` в коде нет (проверено `grep`).

**Доказательство (статика):** см. строки выше. **Подтверждение MITM-прокси — ⏳ (Windows).**

**Рекомендация:** дефолт `AcceptAnyCert=false` и в `ServerConfig`, и в `EngineConfig`, и в
`appsettings.json`. «Принимать любой сертификат» — только явный opt-in для self-hosted в мастере,
с видимым предупреждением. Для продакшн-хоста рассмотреть пиннинг/доверие конкретному CA.

### H1 — IPC: команды управления без аутентификации вызывающего (High) ✅ / DACL ⏳
**Где:** `Engine/Program.cs:71-91` создаёт `NamedPipeServerStream("BarkCloud.Drive.Engine", …,
maxInstances:1, …)` **без** `PipeSecurity`, и привязывает к нему весь `IDriveEngine` через
`JsonRpc.Attach(pipe, engine)`. Поверхность (`Contracts/IDriveEngine.cs`): `LoginAsync`,
`LogoutAsync`, `MountAsync`, `RemountAsync`, `UnmountAsync`, `SetCacheDirAsync`, `ShutdownAsync`.

**Почему проблема:** дефолтный DACL named pipe ограничивает доступ владельцем/сессией (кросс-юзер
блокируется ОС — поэтому это не EoP между пользователями). Но **любой процесс того же пользователя**
может подключиться к pipe в окне, когда UI не подключён (а UI можно закрыть — движок продолжает
жить и обслуживать pipe в цикле `while`), и вызвать `ShutdownAsync` (убить движок/размонтировать),
`SetCacheDirAsync` (увести расшифрованный кэш в произвольный путь — см. M2), `UnmountAsync`.
Перехват пароля через `LoginAsync` ограничен гонкой (pipe занят, пока UI подключён, `maxInstances:1`).

**Доказательство:** `Program.cs:71-91`; цикл обслуживания переживает отключение UI (`:69-91`).
**Точный DACL и сценарий стороннего клиента — ⏳ (PipeList + мини-клиент на Windows).**

**Рекомендация:** задать явный `PipeSecurity` (только текущий пользователь), рассмотреть проверку
peer-процесса; для управляющих команд — токен/секрет согласования между App и Engine.

### H2 — Блокирующий `GetResult()` в синхронных колбэках Dokany (High) ✅ / нагрузка ⏳
**Где:** `Engine/CloudGateway.cs:292` (`EnsureBlock`: `lazy.Value.GetAwaiter().GetResult()` на
Range-GET), `:345` (`ReadWhole`: гидрация целиком); `Engine/BarkCloudFileSystem.cs:446`
(`OpenExistingForWrite`: `DownloadToAsync(...).GetAwaiter().GetResult()`), `:466` (`PersistSession`:
`UploadAsync(...).GetAwaiter().GetResult()`). Все четыре — внутри синхронных колбэков `IDokanOperations`.

**Почему проблема:** колбэки Dokany исполняются на нативных потоках драйвера; блокировка на
`.GetResult()` держит такой поток, пока continuation'ы внутренних `async`-операций исполняются на
.NET ThreadPool. Под конкурентным I/O (рекурсивное копирование дерева, несколько параллельных
читателей больших файлов) это даёт длинные цепочки блокировок и классический риск deadlock/залипания
диска при насыщении пула. `HttpClient.Timeout=Infinite` (M3) усугубляет: зависшая передача держит
поток вечно.

**Доказательство:** строки выше. **Итоговое подтверждение — ⏳:** нагрузка на `X:` +
`dotnet-counters` (ThreadPool Queue Length / Thread Count).

**Рекомендация:** ограничить параллелизм скачиваний/заливок (семафор), задать таймауты на операцию
(см. M3), избегать блокировки потоков Dokan на неограниченных сетевых ожиданиях.

### M1 — Расшифрованный контент в `%TEMP%` без очистки (Medium) ✅
**Где:** кэш по умолчанию `%TEMP%\BarkCloudDrive` (`EngineSettingsStore.cs:21`), рабочие копии записи
`%TEMP%\BarkCloudDrive\write` (`BarkCloudFileSystem.cs:54`). Блоки (`{fileId}.blocks/{N}.blk`,
`CloudGateway.cs:313-322`) и whole-файлы (`{fileId}`, `:358-368`) пишутся и **нигде не удаляются**:
нет эвикции по размеру/TTL, нет очистки при `Logout`/`Unmount`/`Shutdown`, нет очистки на старте.
`SetCacheDir` (`:84-89`) лишь чистит `_downloads` и меняет путь — старые байты остаются.

**Почему проблема:** расшифрованное содержимое облака неограниченно копится на диске в `%TEMP%`
(остаточные данные, рост диска), переживает logout. ACL подпапок `%TEMP%` — ⏳ проверить.

**Рекомендация:** политика кэша (лимит размера/LRU), очистка при logout/unmount и на старте,
осознанный выбор ACL папки кэша.

### M2 — `SetCacheDirAsync` принимает произвольный путь (Medium) ✅
**Где:** `Engine/DriveEngine.cs:171-180` — единственная проверка `string.IsNullOrWhiteSpace`. Путь не
нормализуется и не валидируется (UNC, системная папка, путь вне профиля). В связке с H1 (команда
доступна по IPC) процесс пользователя может направить запись расшифрованного контента в произвольное
место.

**Рекомендация:** валидировать/нормализовать путь (локальный, в пределах профиля пользователя),
отклонять UNC/системные каталоги.

### M3 — `HttpClient.Timeout = InfiniteTimeSpan` (Medium) ✅
**Где:** `Engine/BarkCloudConnection.cs:45`. Сделано сознательно (крупные upload/download не должны
рваться по дефолтным 100 c), но без таймаута зависший сервер/сеть держит поток-колбэк вечно (усиливает
H2). **Рекомендация:** таймаут на операцию/`CancellationToken` (по объёму данных) вместо бесконечного
глобального.

### M4 — `Resolve` — RPC-цепочка на каждый путь/колбэк (Medium) ✅
**Где:** `Engine/CloudGateway.cs:147-201` — `Resolve` идёт по сегментам пути, на каждый зовёт
`ListDirectory` (RPC `ListDirectoryDetailed`, TTL-кэш 5 c). Вызывается из `CreateFile`,
`GetFileInformation` (`BarkCloudFileSystem.cs:224-249`), `ReadFile`, `MoveFile`, `DeleteNode`.
Проводник шлёт пачки метаданных-колбэков → всплески RPC при промахах кэша. **Рекомендация:**
обслуживать `GetFileInformation` из кэша листинга родителя; замерить число `ListDirectoryDetailed`
на типовые операции (⏳).

### L1 — DPAPI без `optionalEntropy` (Low) ✅
`Engine/TokenStore.cs:24-25` — `ProtectedData.Protect(..., optionalEntropy: null, CurrentUser)`.
Любой код в сессии пользователя расшифрует `refresh.bin`. Защита в глубину: добавить
статическую/машинную энтропию. (Прочее по токенам — refresh только в DPAPI, access только в памяти,
`Logout()` чистит память+файл+refresh-loop — **проверено, корректно.**)

### L2 — Несбрасываемые словари + лог без ротации (Low) ✅
`CloudGateway.cs:343-344` (`_downloads`), `:53` (`_wholeMode`), `:51` (`_tempUrls`) — записи не
удаляются; при долгой сессии с многими файлами растёт память. `EngineLog.cs:41` —
`File.AppendAllText` без ротации/лимита размера `engine.log`. **Рекомендация:** очистка
просроченных/завершённых записей; ротация лога по размеру.

### L3 — Мёртвый вывод и хардкод (Low) ✅
`Program.cs:65,94` — `Console.WriteLine` в `WinExe` без консоли (диагностика — через `EngineLog`).
`DeviceIdentity.cs:20` — `AppVersion="0.1.0"` зашит в код (идёт в `x-app-version`). Мелочь, влияет на
диагностику. **Рекомендация:** убрать `Console.WriteLine`; брать версию из сборки.

### L4 — Запуск/убийство движка (Low) ✅
`App/EngineLauncher.cs:52-64` — `KillEngine` бьёт по всем процессам с именем `BarkCloud.Drive.Engine`
(чужие — `AccessDenied`, поймано; в рамках пользователя — норм). `:78-90` — `ResolveEnginePath` в dev
использует `baseDir.Replace("BarkCloud.Drive.App","BarkCloud.Drive.Engine")`; в проде берётся
side-by-side exe. Низкий риск, отметить. (Кавычки автозагрузки `Autostart.cs` обрабатывают пробелы в
пути — **проверено, корректно.**)

### L5 — Нет тестов под `Drive/` (Low) ✅
Каталог `Tests/` покрывает только Backend/Shared; на `Drive/` тестов нет (проверено). Самая хрупкая
логика без покрытия: `Resolve` (посегментный резолв), батч-удаление (`SendChunks`/`RequeueFailed`/
`DropOne`), кэш-«поколения» (`_listGen`+`_listLock`), `HidePending`/тумбстоны. **Рекомендация:**
минимальный набор юнит-тестов на эту логику (она чистая, тестируется без Dokany/сети при инъекции
gRPC-клиентов).

---

## Проверено и признано корректным (negative findings)

- **Хранение токенов:** refresh — только DPAPI (`TokenStore`), access — только в памяти (`TokenManager`);
  `Logout()` атомарно чистит память, останавливает refresh-loop и удаляет `refresh.bin`.
- **Логи без секретов:** ни одного вызова `EngineLog`, пишущего пароль/токен (опровергает предв. §5.4).
- **Проверка статусов HTTP:** блоки требуют `206 PartialContent` (`CloudGateway.cs:310`), upload —
  `EnsureSuccessStatusCode` (`:407`), аватар — `IsSuccessStatusCode` (`:422`); `GetStreamAsync` кидает
  на не-2xx. Подмены ошибочного тела в кэш нет.
- **Конкурентность кэша листинга:** «поколения» (`_listGen`+`_listLock`) корректно отбрасывают
  устаревший ответ листинга, начатый до инвалидации.
- **Дренаж батч-удалений на выходе:** `FlushPending` форсится на `Logout`/`Unmount`/`Shutdown`/
  `DeleteDirectory`/`Dispose`; при сбое сервера файл возвращается в листинг (`DropOne`).
- **Единый источник TLS-флага:** других мест создания `GrpcChannel`/`HttpClient` нет (упрощает фикс C1).

---

## Приоритеты ремедиации

1. **C1** — дефолт `AcceptAnyCert=false` (3 места) + явный opt-in с предупреждением. Однострочный
   класс уязвимости, единый источник — быстрый и высокоэффективный фикс.
2. **H1** — явный `PipeSecurity` + согласование App↔Engine для управляющих команд.
3. **H2 + M3** — семафор на параллелизм скачиваний/заливок + таймауты на операцию.
4. **M1 + M2** — политика и очистка кэша; валидация пути кэша.
5. **M4, L1–L5** — по мере работ; L5 (тесты) окупается при дальнейшем развитии модуля.

> Следующий шаг для полного закрытия плана — прогон ⏳-проверок на Windows-стенде (MITM, нагрузка +
> `dotnet-counters`, PipeList, ProcMon) для эмпирического подтверждения C1/H1/H2/M1/M4.
