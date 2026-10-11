# Аудит backend BarkCloud — 2026-10-10/11

Снимок кода: ветка `t3/68fbc447`, HEAD `b4c48bd`. Отчёт заменяет устаревший корневой `report.md` (он при аудите не использовался).
Пути — от корня репозитория. Номера строк сверены на `b4c48bd`; после правок могут сдвинуться — ищите по символам.

## Как пользоваться (для агента исправлений)

- Каждая находка: **ID · severity · сервис**, место, почему это проблема, предусловия, фикс, acceptance-тест.
- Уровень доказательства:
  - **[R]** — воспроизведено локально probe-программой (без сети/production);
  - **[S+]** — статически, прошло независимое ревью-перепроверку;
  - **[S]** — статически; второй независимый проход подтвердил дефект, поправки к severity/фиксу внесены в текст.
- Probe для воспроизведения: `Docs/audit/backend-2026-10-10/WebProbe/` и `.../TorrentProbe/` (см. их README). После исправления их вывод должен измениться.
- Порядок работ — раздел «Приоритет исправлений» в конце.
- Обязательно при исправлениях:
  - правки в `Backend/BarkCloud.Web` (включая `ClientApp`) → поднять `AppVersion.Current` в `Backend/BarkCloud.Web/AppVersion.cs`;
  - изменился контракт/поток → обновить заметку в `Obsidian/BarkCloudVault/` (Backend/*, Web/*);
  - API MassTransit/ImageSharp/MonoTorrent сверять по Context7 перед кодом.

## Сводная таблица

| ID | Sev | Сервис | Кратко | Доказ. |
|----|-----|--------|--------|--------|
| WEB-1 | **High** | Web (+Files) | Stored XSS: SVG отдаётся через анонимный same-origin прокси `/api/files/image` | R + S+ |
| TOR-1 | **High** | Torrent | Path traversal в путях torrent (v2 file tree, v1 single-file `name`) → запись/чтение вне каталога пользователя | R |
| IDN-1 | **High** | Identity | Перебор TOTP через `ResetPassword(Authenticator)`: нет лимита попыток на аккаунт | S+ |
| IDN-2 | **High** | Identity | Регистрация passkey без step-up и без уведомления | S+ |
| MQ-1 | Medium–High | Identity, Files, Torrent, Notification | У всех MassTransit-консьюмеров (кроме upload processing) нет retry/redelivery | S+ |
| FIL-1 | Medium–High | Files | Неограниченная суммарная буферизация resumable parts на диск | S+ |
| WEB-2 | Medium | Web | `/api/head/icon` читает ответ целиком до проверки лимита (до ~2 GiB на запрос) | R + S+ |
| WEB-3 | Medium | Web | Безлимитный multipart на анонимных формах (`/login`, `/register`, `/forgot`) | R + S+ |
| IDN-3 | Low–Medium | Identity/Users | Перечисление пользователей: `UserNotFoundException` пробрасывается из `Auth`/`ResetPassword` | S |
| IDN-4 | Medium | Identity | `SetPassword`/`ForceSetPasswordServer` не отзывают другие сессии | S |
| IDN-5 | Low–Medium | Users | `DeleteAccount` без повторной аутентификации | S |
| IDN-6 | Medium | Identity | Lockout-DoS: счётчик попыток на аккаунт резервируется до проверки пароля | S+ |
| PLT-1 | Medium (hardening) | nginx, все gRPC | Служебные API и gRPC reflection доступны снаружи; сервисные JWT на 10 лет | S+ |
| FIL-2 | Medium | Files | Сбой финального commit оставляет resumable-сессию в `Processing` навсегда | S+ |
| FIL-3 | Medium | Files | ImageSharp 3.1.12: BigTIFF-зацикливание декодера (GHSA-wmxv-xphr-5c9g) + ещё 4 advisory | S |
| FIL-4 | Low–Medium | Files | `GetUploadUrl` создаёт неубираемые placeholder-строки | S |
| CFG-1 | Medium | Configuration | Смена `JwtSettings:SecretKey` из админки не перевыпускает `*Service:Token` → Users/Files/Torrent не стартуют | S+ |
| TOR-2 | Low–Medium | Torrent | Blind SSRF через tracker URL из magnet/.torrent | S |
| TOR-3 | Low | Web→Torrent | `ImportToCloud` не отменяется при отключении клиента | S |
| TOR-4 | Medium | Torrent | `RemoveTorrent(DeleteFiles=true)` без manager удаляет запись, файлы остаются | S |
| TOR-5 | Medium | Torrent | Ошибка старта manager оставляет его в registry | S |
| TOR-6 | Medium | Torrent | `UserDeletedConsumer` проглатывает ошибку удаления каталога и удаляет retry-состояние | S |
| TOR-7 | Low–Medium (perf) | Torrent | Progress stream каждые 1,5 с грузит все torrents с blob и файлами | S |
| TOR-8 | Low | Torrent | Гонка `SetFilePriority` → дубли строк приоритета (только magnet) | S |
| TOR-9 | Low–Medium | Torrent | Общий engine + проверка дубликата по пользователю: B не может добавить torrent, который есть у A, и узнаёт об этом | S |
| WEB-4 | Medium–Low | Web | Admin-cookie не привязана к пользователю и переживает logout | R + S+ |
| IDN-7 | Medium–Low | Identity | Регистрационные коды не гасятся после подтверждения | S+ |
| IDN-8 | Medium–Low | Users/Android | `DeleteDevice` не отзывает сессию, Android использует его как «удалить устройство» | S |
| GRP-1 | Low–Medium | GrpcServer | `ServerExceptionInterceptor` превращает любой `RpcException` в `Unknown` и отдаёт `ex.Message` | S+ |
| IDN-9 | Low | Identity | OTP-письма публикуются напрямую, не через outbox | S+ |
| WEB-5 | Low | Web | Валидатор icon-прокси: fallback на `Request.Host`, порт игнорируется, редиректы разрешены | R + S+ |
| GRP-2 | Low (латентный) | Shared.Exceptions | `ExceptionClientInterceptor` кэширует экземпляры исключений, теряет metadata | S+ |
| IDN-L* | Low | Identity/Users | 12 мелких проблем (см. раздел Low) | S |
| WEB-6 | Info | Web | Переполнение `exp` в admin-cookie → 500 на собственный запрос | R |

---

## High

### WEB-1 · High · Web (+Files) — Stored XSS через SVG на origin Web [R + S+]

**Где:**
- `Backend/BarkCloud.Web/WebEndpoints.cs:135-171` — анонимный `GET /api/files/image`: берёт GUID из `url`, качает из Files, проверяет только `contentType.StartsWith("image/")` (`:159`), выставляет этот `Content-Type` (`:162`) и копирует тело. `Content-Disposition` из Files не пробрасывается, `X-Content-Type-Options`/CSP не ставятся (в Backend нет ни одного `Content-Security-Policy`).
- `Backend/BarkCloud.Files/Features/DownloadFile/DownloadFileCommandHandler.cs:89` — тип определяется по расширению имени; `Backend/BarkCloud.Files/Extensions/FileExtensions.cs:26` — `.svg → image/svg+xml`.
- SVG попадает в хранилище обоими путями загрузки:
  - legacy: `UploadFileCommandHandler.cs:316-337` — ошибка ImageSharp-декодера проглатывается, файл принимается;
  - resumable: `UploadSessionCoordinator.cs:399-418` + `UploadFileCommandHandler.cs:107-113` — `ContentTypeOverride` берётся из клиентского `CreateUploadSession.ContentType` без сверки с расширением; при `text/*` формат не проверяется.
- Temp-ссылку на свой файл выдаёт `GetTempDownloadUrlCommandHandler.cs:47-60` (TTL 60 мин).

**Почему проблема:** любой пользователь с аккаунтом загружает SVG со скриптом и отправляет ссылку на `/api/files/image?...`. Документ открывается на origin Web, скрипт выполняет same-origin запросы с cookie жертвы: файлы, `/api/*`, удаление аккаунта. Если у жертвы открыт admin-gate (WEB-4) — админ-операции. В связке с IDN-2 скрипт регистрирует свой passkey и получает постоянный вход.

**Предусловия:** аккаунт у атакующего (при открытой регистрации — любой), жертва открывает ссылку.

**Фикс:**
1. `/api/files/image`: allowlist растровых типов (`image/jpeg|png|gif|webp|avif|bmp`), остальное (включая `image/svg+xml`) → 415. Всегда ставить `X-Content-Type-Options: nosniff` и `Content-Security-Policy: default-src 'none'; sandbox`.
2. Files `DownloadFile`: активные типы (`image/svg+xml`, `text/html`, `application/xml`, `text/javascript`) отдавать как `application/octet-stream` или с `CSP: sandbox`.
3. Resumable: сверять `content_type` клиента с расширением (или игнорировать клиентский тип для классификации).
4. Defense-in-depth: глобальная CSP для SPA.

**Acceptance:** тест Web со stub-upstream `image/svg+xml` → 415; `image/png` → 200 с `nosniff` и CSP. Тест Files: temp-download `.svg` не возвращает `image/svg+xml` без sandbox. WebProbe печатает `SVG_PROXY status=415` (или `script=False`).

### TOR-1 · High · Torrent — path traversal в путях файлов torrent [R]

**Где:**
- `Backend/BarkCloud.Torrent/Host/TorrentApiService.cs:95-98` — `Torrent.LoadAsync` пользовательского файла; `:118-128` — `torrent.Files[i].Path` сохраняется в `TorrentFileEntity.Path` без проверки.
- `Backend/BarkCloud.Torrent/Infrastructure/TorrentEngineService.cs:114-130` — `Engine.AddAsync(torrent, savePath)`.
- `Backend/BarkCloud.Torrent/Host/TorrentController.cs:42-58` — `Download` берёт `Manager.Files[file].FullPath` либо `Path.Combine(entity.SavePath, row.Path)` и отдаёт `PhysicalFile` без проверки, что путь внутри каталога пользователя.
- Пакет `MonoTorrent` 3.0.2 (`BarkCloud.Torrent.csproj:22`): v1 `PathValidator` не применяется к v2 file tree и к `name` v1 single-file.

**Почему проблема:** `FullPath`, по которому engine пишет данные и `Download` читает, выходит за `Torrent:DownloadPath/{userId}`. Отсюда перезапись и чтение файлов в пределах прав процесса Torrent, включая каталоги других пользователей.

**Воспроизведено** (`Docs/audit/backend-2026-10-10/TorrentProbe`, реальный MonoTorrent 3.0.2, менеджеры не запускались):
- принято 9 из 11 вариантов;
- отклоняются только `..` и rooted-пути в `files[].path` v1 multifile;
- v2 file tree с `..` и rooted-компонентом, а также v1 single-file `name` с `..`/rooted дают `FullPath` вне `savePath`;
- fallback `TorrentController.Download` вернул `PhysicalFileResult` вне каталога пользователя.

Rooted-варианты проверены на Windows; на Linux-контейнере перепроверить.

**Предусловия:** авторизованный пользователь Torrent API.

**Фикс:** одна функция проверки относительного пути: отвергать rooted/drive/UNC, компоненты `.`/`..`; после `Path.GetFullPath(Path.Combine(root, rel))` результат обязан начинаться с канонического `root + separator`. Применять:
- в `AddTorrentFile`/`AddMagnet` до сохранения и до engine (для magnet — после получения metadata);
- в `TorrentStartupService` при восстановлении строк из БД;
- в обеих ветках `TorrentController.Download` (и для `FullPath` менеджера).

Для v1 single-file валидировать и `name`. Symlink-escape на целевой ОС — по возможности.

**Acceptance:**
- v2 с `..`/rooted и v1 single-file `name` = `..`, `../x`, абсолютный путь — отклоняются до store/engine;
- `Download` не возвращает файл вне корня для активного manager и fallback-строки;
- обычные v1/v2 торренты работают;
- TorrentProbe перестаёт печатать `controllerFallbackEscapesUserDir=True`.

### IDN-1 · High · Identity — перебор TOTP через сброс пароля [S+]

**Где:**
- `Backend/BarkCloud.Identity/Features/ResetPassword/ResetPasswordCommandHandler.cs:92` — единственный лимит `ResetPasswordByIp`; `:135-160` — для `OtpType.Authenticator` каждый раз новый reset-id на 15 мин, без лимита на аккаунт и без письма.
- `Backend/BarkCloud.Identity/Features/ConfirmResetPassword/ConfirmResetPasswordCommandHandler.cs:84` — лимит по IP; `:129` — 5 попыток на **reset-id**; `:147` — `VerifyTotp`.
- `EnsureTotpAttemptAsync` (общий счётчик `OtpByAccount`) здесь не вызывается — только в `ConfirmOtpVerification`/`EnableOtpVerification`/`DisableOtpVerification`.

**Почему проблема:** без пароля и почты, зная только логин жертвы с включённым TOTP, атакующий бесконечно получает новые reset-id и по 5 попыток на каждый. При попадании — новый пароль, сессия, отзыв сессий жертвы.

Оценка: с одного нормализованного источника ≈50 попыток/час, 50 % успеха примерно за 190 дней. Время падает линейно с числом источников; IPv6 нормализуется до `/64` (`GrpcServer/Tracker/SourceIpResolver.cs:38`), поэтому при большом префиксе или ботнете — существенно быстрее. Это оценка при допущениях, не измерение.

**Фикс:**
1. В `ConfirmResetPassword` для Authenticator вызывать `EnsureTotpAttemptAsync(userId)` до `VerifyTotp`.
2. В `ResetPassword` для Authenticator лимит выдачи на аккаунт; при активном reset-id возвращать его же.
3. Письмо владельцу о попытке сброса через Authenticator.

Компромисс: счётчик на аккаунт позволяет выжигать TOTP-попытки жертвы. Это лучше распределённого перебора; держать его отдельно от счётчика паролей (см. IDN-6).

**Acceptance:** более 10 неверных TOTP на аккаунт за 15 мин через любые reset-id и IP → `TooManyRequests`; unit-тест на `ConfirmResetPasswordCommandHandler`.

### IDN-2 · High · Identity — регистрация passkey без step-up [S+]

**Где:**
- `Backend/BarkCloud.Identity/Host/IdentityApiService.cs:219-234` — `BeginWebAuthnRegistration`/`CompleteWebAuthnRegistration` только с policy `User`.
- `Features/CompleteWebAuthnRegistration/CompleteWebAuthnRegistrationCommandHandler.cs:25-99` — нет reauth, нет уведомления в outbox.
- Web проксирует: `Backend/BarkCloud.Web/Endpoints/SettingsEndpoints.cs:302-318`.
- Для сравнения: изменение 2FA требует пароль (`ReauthPasswordVerifier`).

**Почему проблема:** краткий доступ (украденный access-токен, XSS из WEB-1, разблокированное устройство) превращается в постоянный passwordless-вход, который переживает смену пароля и обходит TOTP. Владелец ничего не узнаёт.

Сохранение легитимных ключей после сброса пароля само по себе **не баг**. Удаление ключей при сбросе — продуктовое решение (опция), не обязательный фикс.

**Фикс:**
- step-up (пароль, а при наличии — TOTP/email-код) на `BeginWebAuthnRegistration`;
- письмо «добавлен ключ» (и «удалён ключ») через `NotificationOutbox` в одной транзакции с `AddCredential`/remove;
- дата добавления ключа в списке.

**Acceptance:** регистрация без свежего step-up → отказ; при успехе — запись в `PendingNotifications`.

---

## Medium–High

### MQ-1 · Medium–High · Identity, Files, Torrent, Notification — консьюмеры без retry [S+]

**Где** (`rg UseMessageRetry|UseDelayedRedelivery Backend` — пусто; retry есть только у upload processing через `UseScheduledRedelivery`, `Files/Scheduling/UploadProcessingQueue.cs:62`):
| Endpoint | Конфиг | Консьюмер |
|---|---|---|
| `notifications-email-handler` | `Backend/BarkCloud.Notification/Program.cs:38` | `Consumers/EmailQueueConsumer.cs` — `throw; // MassTransit обработает retry` (`:45-54`), комментарий не соответствует конфигурации |
| `user-deleted-identity` | `Backend/BarkCloud.Identity/Program.cs:121` | `Consumers/UserDeletedConsumer.cs:34-44` |
| `user-deleted-files` | `Backend/BarkCloud.Files/Program.cs:173` | `UserDeletedConsumer` |
| `user-deleted-torrent` | `Backend/BarkCloud.Torrent/Program.cs:83` | `Consumers/UserDeletedConsumer.cs` (см. также TOR-6) |

**Почему проблема:** без retry-middleware любое исключение консьюмера сразу отправляет сообщение в `{queue}_error`.
- Notification: одиночный транзиентный сбой SMTP (таймаут, greylisting 4xx) молча теряет письмо: OTP, «вход выполнен», «2FA отключена», «пароль изменён».
- Identity: сбой БД в `UserDeletedConsumer` оставляет refresh-токены удалённого пользователя (живут 9999 дней). `CreateTokenCommandHandler.cs:19-36` не проверяет существование пользователя — удалённый аккаунт продолжает получать user JWT.
- Files/Torrent: данные удалённого пользователя остаются.

**Фикс:**
- `e.UseMessageRetry(r => r.Exponential(...))` + `UseDelayedRedelivery` (нужен scheduler — в Files уже есть) на всех четырёх endpoint'ах;
- для SMTP 5xx — `Ignore` (сразу в `_error`);
- алерт/метрика на `_error`-очереди;
- defense-in-depth для Identity: tombstone удалённого пользователя или проверка в `CreateToken`.

API сверить по Context7 (`/masstransit/masstransit`).

**Acceptance:** MassTransit test harness — первый вызов бросает транзиентную ошибку, повтор успешен, `Fault` не опубликован; для Identity — после повтора refresh удалённого пользователя отклоняется.

### FIL-1 · Medium–High · Files — неограниченная буферизация resumable parts [S+]

**Где:**
- `Backend/BarkCloud.Files/Host/FilesController.cs:34-35` — `[DisableRequestSizeLimit]` на `UploadSessionPart`.
- `Services/UploadSessionCoordinator.cs:243-283` — проверяет статус/токен/размер части, но не резервирует слот или байты.
- `Infrastructure/S3MultipartUploadStore.cs:60-70` — для не-R2 профилей вся часть (до ~525 MiB, `UploadSessionCoordinator.cs:35-38,378-386`) буферизуется во временный файл.
- `LegacyUploadBudget` применяется только к legacy POST.
- nginx `/file-upload/` без `limit_conn` (`Tools/BarkCloud.Builder/BackendComposeGenerator.cs:694-707`); Kestrel Files без `MinRequestBodyDataRate` (`Program.cs:35-43`).

**Почему проблема:** один аутентифицированный пользователь параллельными (и медленными) PUT разных или одинаковых частей одной сессии заполняет temp-том Files → отказ загрузок у всех.

**Фикс:** admission budget для resumable parts (лимит параллельных запросов и суммарных буферизуемых байт на процесс и пользователя/сессию); lease до чтения тела, освобождение при успехе/ошибке/отмене; `limit_conn` на `/file-upload/`.

**Acceptance:** сверхлимитный PUT отклоняется до чтения тела; суммарный temp не превышает бюджет; отмена освобождает lease (интеграционный тест).

---

## Medium (и пониженные после перепроверки)

### WEB-2 · Medium · Web — `/api/head/icon` буферизует весь ответ [R + S+]

**Где:** `Backend/BarkCloud.Web/WebEndpoints.cs:101-128` — анонимный; `ReadAsByteArrayAsync` (`:117`) до проверки `> 2_097_152` (`:118`).

**Почему проблема:** валидатор (WEB-5) разрешает публичный Files-URL `/download/<tempId>`. Атакующий с аккаунтом загружает большой файл с «картиночным» типом (см. WEB-1, пути загрузки). Дальше анонимные запросы заставляют Web держать в памяти до ~2 GiB на каждый → OOM контейнера.

Probe: 3 MiB прочитаны полностью до ответа 413. Достижимость Files-URL из контейнера Web (hairpin) — свойство деплоя, не проверялось.

**Фикс:** проверять `Content-Length` до чтения; читать через ограниченный поток с обрывом на 2 MiB + 1.

**Acceptance:** upstream 3 MiB → 413, прочитано ≤ 2 MiB + буфер; без `Content-Length` — так же.

### WEB-3 · Medium · Web — безлимитный multipart на анонимных формах [R + S+]

**Где:**
- `Backend/BarkCloud.Web/Program.cs:106-111` — глобально `MaxRequestBodySize=null`, `MinRequestBodyDataRate=null`, `MultipartBodyLengthLimit=long.MaxValue`.
- nginx Web: `client_max_body_size 0`, `proxy_request_buffering off` (`BackendComposeGenerator.cs:687-727`, `Backend/nginx/cloud.barkfluff.conf:224-262`).
- `POST /login` (`WebEndpoints.cs:42-44`) вызывает `ReadFormAsync()` до каких-либо проверок; `/register*`, `/forgot` (`:260-265`) — после проверки флагов конфигурации, не аутентификации.

**Почему проблема:** файловые секции формы ASP.NET Core пишет во временные файлы. Поток анонимных медленных запросов держит соединения и temp-диск (до `client_body_timeout` 7200 с). DoS транзиентный, но анонимный.

**Фикс:** снять глобальные лимиты; вернуть безлимит только на upload-endpoint'ах (`[RequestSizeLimit]`/`IHttpMaxRequestBodySizeFeature`). Для форм аутентификации — `FormOptions` ≤64 KiB без файлов. В nginx — `client_max_body_size 1m` на `location /`, отдельный `location` для upload.

**Acceptance:** `POST /login` multipart 2 MiB → 413 до записи temp; загрузки работают.

### IDN-3 · Low–Medium · Identity/Users — перечисление пользователей [S]

**Где:**
- `Backend/BarkCloud.Users/Features/FindByLogin/FindByLoginQueryHandler.cs:51` — при отсутствии пользователя `throw new UserNotFoundException()` (пустой ответ не возвращается никогда).
- `ServerExceptionInterceptor` → `FailedPrecondition` + `x-error-code`; `ExceptionClientInterceptor` в Identity пересоздаёт `UserNotFoundException`.
- `Backend/BarkCloud.Identity/Features/Auth/AuthCommandHandler.cs:79` — исключение не ловится, ветка `if (user.User is null)` → `InvalidLoginOrPassword` (`:81-91`) **недостижима**.
- `Features/ResetPassword/ResetPasswordCommandHandler.cs:107` — то же: анти-энумерация с фейковым reset-id (`:109-120`) недостижима. Дополнительно (`:124-132`): существующий пользователь без TOTP при `Authenticator` → `OtpNotCreatedException`, а несуществующий получил бы фейковый id — ещё один оракул (раскрывает включённость TOTP).
- Тесты мокают несуществующий контракт: `AuthCommandHandlerTests.cs:137-153`, `ResetPasswordCommandHandlerTests.cs:80-84` (`new FindByLoginResponse()` вместо исключения).

**Почему проблема:** код ошибки (и время ответа — на ветке «не найден» нет bcrypt) различает «нет пользователя» и «неверный пароль».

Web показывает разный текст ошибки (`Backend/BarkCloud.Web/Auth/AuthGateway.cs:151`).

Severity понижена: анонимные `UsersApi.CheckExistEmail/CheckExistUsername` без rate-limit (`UsersApiService.cs:63-80`) — более сильный оракул, а `Auth` ограничен по IP. Главная ценность фикса — закрыть оракул «включён ли TOTP» и тайминг. Rate-limit/капча для `CheckExist*` — продуктовое решение.

**Фикс:** в `Auth`/`ResetPassword` ловить `UserNotFoundException` и идти в ветку «не найден»; в `Auth` — фиктивный `BCrypt.Verify`; в ResetPassword(Authenticator) без TOTP — тоже фейковый id; моки тестов → `ThrowsAsync(new UserNotFoundException())`.

**Acceptance:** unit-тесты с бросающим моком: `Auth` → `InvalidLoginOrPasswordException`, `ResetPassword` → фейковый reset-id.

### IDN-4 · Medium · Identity — смена пароля не отзывает сессии [S]

**Где:**
- `Features/SetPassword/SetPasswordCommandHandler.cs:23,30,36` — `IRefreshTokensStorage` внедрён, но не используется; `:82-100` меняет только хеш.
- `Features/ForceSetPasswordServer/ForceSetPasswordServerCommandHandler.cs:38-48` — то же; без проверки существования пользователя и пустого пароля.
- Для сравнения: `ConfirmResetPasswordCommandHandler.cs:224` (`RevokeAllSessions`).

**Почему проблема:** после компрометации пользователь (или админ) меняет пароль, а refresh-токены атакующего (9999 дней) продолжают работать.

**Фикс:** в той же транзакции отзывать все сессии, кроме текущей (SetPassword), или все (ForceSet).

**Внимание:** существующий `RevokeAllSessions(userId, currentDeviceId)` (`Persistence/Services/RefreshTokensStorage.cs:82-94`) удаляет refresh **всех** устройств, включая текущее: `currentDeviceId` влияет только на возвращаемый счётчик. Нужен отдельный метод «все, кроме текущего устройства» либо выдача новой пары текущему устройству с перестановкой cookie в Web. `SetPassword` вызывается и при регистрации (`Backend/BarkCloud.Web/Auth/RegistrationGateway.cs:145`) — не сломать этот путь. У `ForceSetPasswordServer` вызывающих в backend нет — эта часть латентная.

**Acceptance:** refresh других устройств после смены → `InvalidRefreshToken`.

### IDN-5 · Low–Medium · Users — `DeleteAccount` без повторной аутентификации [S]

**Где:** `Backend/BarkCloud.Users/Host/UsersApiService.cs:129-134`, `Features/DeleteAccount/DeleteAccountCommandHandler.cs:39-44`; `DeleteAccountRequest` пустой (`Shared/BarkCloud.Proto/users_api.proto`).

**Почему проблема:** владелец access-токена (XSS из WEB-1, украденное устройство) необратимо удаляет аккаунт и файлы.

CSRF закрыт `SameSite=Lax` (`AuthGateway.cs:296`): нужен уже украденный токен или XSS — это hardening. Web-endpoint: `Backend/BarkCloud.Web/SettingsEndpoints.cs:383-389`.

**Фикс:** требовать подтверждение (пароль/TOTP/email-код) через служебный RPC Identity или перенести инициирование удаления в Identity. Учесть пользователей без пароля (passkey-only) — для них email-код.

**Acceptance:** без подтверждения → ошибка, данные целы.

### IDN-6 · Medium · Identity — lockout-DoS по аккаунту [S+]

**Где:** `Features/Auth/AuthCommandHandler.cs:97` — `LoginByAccount` резервируется до проверки пароля; `:269` — сброс только при успехе; `Services/AuthLimits.cs:24` — 10 / 15 мин.

**Почему проблема:** 10 запросов раз в 15 минут с одного IP (укладывается в лимит по IP) бессрочно блокируют вход по паролю конкретному пользователю, даже с верным паролем.

**Фикс:** **не** переходить на чисто per-IP лимит — это вернёт распределённый перебор. Варианты:
- раздельные счётчики «неверные» и «все попытки»;
- прогрессивная задержка вместо жёсткого блока;
- верный пароль + второй фактор с нового источника проходит;
- уведомление владельцу.

**Acceptance:** 10 неверных попыток с IP A не мешают входу с верным паролем + 2FA с IP B; общий перебор по аккаунту ограничен.

### PLT-1 · Medium (hardening) · nginx + все gRPC — служебные API наружу [S+]

**Где:**
- `Backend/nginx/cloud.barkfluff.conf:50,62-63` (Identity) и `:75,87-88` (Users) — `location / { grpc_pass }` проксирует всё, включая `IdentityServerApi`, `SessionRevocationApi`, `UsersServerApi`. Проверить и генератор `Tools/BarkCloud.Builder/BackendComposeGenerator.cs`.
- `MapGrpcReflectionService()` без авторизации: `Identity/Program.cs:142`, `Users/Program.cs:97`, `Files/Program.cs:192`, `Torrent/Program.cs:98` (Configuration `:189` наружу не проксируется).
- Сервисные токены на 10 лет: `Configuration/Infrastructure/ConfigurationDefaultsPopulator.cs:231,355-374`, `Web/Infrastructure/ServiceToken.cs:30,38`.
- `GrpcServer/XAuth/XAuthExtensions.cs:56` — отзыв проверяется только для `TokenType.User`.
- Один симметричный `JwtSettings:SecretKey` у всех сервисов.

**Почему проблема:** анонимного доступа нет (policy `Service`). Но утечка любого сервисного токена из логов/БД/ошибки = админ-доступ из интернета (`CreateSessionForUserServer`, `ForceSetPasswordServer`) без возможности отзыва, кроме ротации секрета. А ротация ломает сервисы (CFG-1).

**Фикс:**
- nginx: allowlist только пользовательских сервисов (`/barkcloud.identity.IdentityApi/`, `/barkcloud.users.UsersApi/`, …), остальное deny;
- reflection — только в Development;
- в перспективе: короткоживущие сервисные токены, асимметричная подпись user JWT только в Identity.

**Acceptance:** `grpcurl` снаружи к `*ServerApi/*` и reflection → отказ; внутренние вызовы работают.

### FIL-2 · Medium · Files — вечный `Processing` при сбое финального commit [S+]

**Где:**
- `Backend/BarkCloud.Files/Services/UploadSessionProcessor.cs:68-123` — try/catch охватывает только pipeline; `:125-134` — транзакция перевода в `Ready` вне catch. Ошибка `BeginTransaction/SaveChanges/Commit` не попадает ни в ветку исчерпания попыток, ни в `processing_retry`.
- После 5 доставок (`Scheduling/UploadProcessingQueue.cs:14-20,62`) сообщение уходит в `_error`.
- `UploadSessionMaintenance.cs:65-70` восстанавливает `Processing` только для legacy (`MultipartUploadId == ""`); повторный `Complete` не republish'ит (`UploadSessionCoordinator.cs:619-636`).

**Почему проблема:** сессия навсегда в `Processing`. Держит резерв квоты (`StorageQuotaService.cs:36`) и считается активной для cutover миграции хранилища (`StorageMigrationGate.cs:261`) — может блокировать миграцию.

**Фикс:**
- включить финализацию в retry/recovery-логику;
- maintenance для resumable `Processing` старше порога: идемпотентный republish или `Failed` + снятие резерва + cleanup.

**Acceptance:** fault-injection на финальный commit → сессия в итоге `Ready` или `Failed`, резерв снят; replay не создаёт лишних `FilePreview`/блобов.

### FIL-3 · Medium · Files — уязвимый ImageSharp 3.1.12 [S]

**Где:** `Backend/BarkCloud.Files/BarkCloud.Files.csproj:32`.
- Достижимо: GHSA-wmxv-xphr-5c9g — повреждённый BigTIFF зацикливает декодер. `.tif/.tiff` → `image/tiff` (`Extensions/FileExtensions.cs:21-29`) → `Services/ImageCompressor.cs:12-23` (`Image.IdentifyAsync`, без таймаута).
- `UploadProcessingQueue.cs:56-63` — 2 параллельные обработки → два таких файла останавливают очередь подготовки файлов до рестарта.
- Ещё 4 advisory (GHSA-gwg2-r3hj-4w44, -j3p4-wp97-rph4, -j9gm-c75j-xc9q, -jjfr-hcj7-qf5w) — call site в коде не найден.

ImageSharp определяет формат **по содержимому**, а не по расширению: TIFF под именем `.png` дойдёт до TIFF-декодера. Декодер по умолчанию стоит на всех `image/*` и аватарах (`UploadAvatarServerCommandHandler.cs:61,70`). Поэтому для остальных 4 advisory корректная формулировка — «достижимость не установлена», а не «нет call site». Неподтверждённое сообщение после рестарта будет доставлено снова — «до рестарта» оптимистично.

**Фикс:** по advisory затронуто `>=2.0.0, <=4.1.1`, исправлено только в **4.1.2**, патча в ветке 3.1.x нет. Значит, нужен мажорный апгрейд: проверить breaking changes API и условия лицензии по release notes/Context7. Пока апгрейд не готов — передавать в декодирование `DecoderOptions` с собственной `Configuration` **без TIFF-модуля** (отключение по расширению не поможет). Таймаут — только дополнительная мера: зацикленный декодер может не проверять токен отмены.

**Acceptance:** `dotnet list package --vulnerable` чист; тест с малформированным BigTIFF завершается контролируемой ошибкой, следующее задание обрабатывается.

### FIL-4 · Low–Medium · Files — неубираемые placeholder'ы от `GetUploadUrl` [S]

**Где:**
- `Features/GetUploadUrl/GetUploadUrlCommandHandler.cs:41-65` — сохраняет `UploadFile` с владельцем до загрузки, без сессии и квоты.
- Уборка не видит такие строки: `UploadSessionMaintenance.cs:21-45` — только по сессиям; `OrphanBlobCleanupService.cs:72-77` — только `Uploaders.Count == 0`.
- Квота их не учитывает (`StorageQuotaService.cs:27-33`).

**Почему проблема:** любой пользователь бесконечно раздувает `UploadedFiles` и индексы.

Такие же строки остаются и при сбое legacy POST (`Backend/BarkCloud.Torrent/Infrastructure/TorrentImportService.cs:66-79`, `Backend/BarkCloud.Web/Endpoints/CloudApiEndpoints.cs:1307`).

**Фикс:** TTL-cleanup placeholder'ов без сессии и без `UploadedAt` (под блокировкой, с учётом гонки с POST) или выдача URL как ограниченной сессии.

**Acceptance:** URL-only строки удаляются после TTL; активный POST не затрагивается.

### CFG-1 · Medium · Configuration — ротация JWT-секрета ломает старт сервисов [S+]

**Где:**
- `JwtSettings:SecretKey` редактируется из админки (`Backend/BarkCloud.Configuration/Catalog/SettingsCatalog.cs:77`; `SettingsValueValidator.cs:15-16` — только длина).
- `*Service:Token` генерируются только если пусты (`Infrastructure/ConfigurationDefaultsPopulator.cs:117-146,229-231`).
- Users/Files/Torrent с токеном старого ключа падают на `RevocationSyncService.StartAsync` (`GrpcServer/XAuth/RevocationSyncService.cs:20-39`, 6 попыток → исключение). Web не затронут (сам подписывает токен).

**Почему проблема:** одна правка в админке + рестарт кладёт mesh. Ручное восстановление — очистить `*Service:Token` и перезапустить Configuration; UI об этом не предупреждает.

**Фикс:** при изменении секрета в той же транзакции перевыпускать все `*Service:Token`; пометить настройку «требует рестарта всех сервисов».

**Acceptance:** тест: `UpdateValue(SecretKey)` → все `*Service:Token` валидны с новым ключом.

### TOR-2 · Low–Medium · Torrent — blind SSRF через tracker URL [S]

**Где:** `Backend/BarkCloud.Torrent/Host/TorrentApiService.cs:56-80` (magnet), `:95-133` (.torrent); `Infrastructure/TorrentEngineService.cs:95-130`. MonoTorrent ходит по `tr`/announce через `HttpClient` без политики назначения.

**Почему проблема:** пользователь заставляет сервис делать HTTP GET на loopback/приватные/link-local адреса внутренней сети. Это сканирование и побочные эффекты GET; ответ пользователю не возвращается.

Дополнительные возможные векторы (UDP-трекеры, peer-адреса и webseed из metadata) на MonoTorrent 3.0.2 не проверены — их закрывает только сетевая политика.

**Фикс:** egress-политика сети контейнера Torrent (запрет приватных диапазонов, кроме нужных) или проверка адресов на момент соединения (устойчивая к DNS rebinding). Политику для private trackers задокументировать.

**Acceptance:** tracker на loopback/private → соединение не выполняется; внешний tracker работает.

### TOR-3 · Low · Web→Torrent — импорт не отменяется при отключении клиента [S]

**Где:** `Backend/BarkCloud.Web/Endpoints/TorrentApiEndpoints.cs:105-113` — `ImportToCloudAsync(req, token)` без `http.RequestAborted`.

**Почему проблема:** закрытая вкладка не останавливает долгий импорт → лишняя нагрузка. Зависших reservation нет: импорт просто доходит до конца. Токен отмены не передаётся ни в одном torrent-endpoint (`:30-112`).

**Продуктовое решение:** отменять ли импорт при закрытии вкладки. Если отменять — отмена на середине оставит placeholder'ы (FIL-4), чинить вместе.

**Фикс:** передать `cancellationToken: http.RequestAborted`. Поднять `AppVersion`.

**Acceptance:** обрыв HTTP отменяет gRPC; незавершённый файл не остаётся placeholder'ом.

### TOR-4 · Medium · Torrent — удаление без manager теряет учёт файлов [S]

**Где:** `Host/TorrentApiService.cs:394-399`; `Infrastructure/TorrentEngineService.cs:144-188` — `RunExclusiveAsync` возвращает `false` без manager (`:171-172`), результат не проверяется; `TorrentStartupService.cs:42-61` оставляет строку при неудачном restore.

**Почему проблема:** `DeleteFiles=true` удаляет строку, а файлы остаются без пути к очистке.

**Внимание:** отдельного каталога у torrent **нет**. `SavePath` общий для всех торрентов пользователя (`TorrentApiService.cs:52-53`, `{DownloadPath}/{userId}`). У magnet-торрентов нет строк файлов (их создаёт только `SetFilePriority`). «Очистить каталог торрента» удалит чужие торренты того же пользователя.

**Фикс:**
- для новых торрентов — подкаталог на каждый torrent (`{userId}/{torrentId}`) или сохранение полного списка файлов после получения metadata;
- при отсутствии manager удалять только эти пути (с проверкой из TOR-1), строку — после успешной очистки;
- для существующих записей без списка файлов безопасной очистки нет — оставлять строку и возвращать ошибку.

**Acceptance:** inactive torrent удаляется вместе со своими файлами, соседние торренты пользователя целы; при ошибке очистки строка остаётся для retry.

### TOR-5 · Medium · Torrent — manager остаётся в registry после ошибки старта [S]

**Где:** `Infrastructure/TorrentEngineService.cs:127-130` — `Track` до `StartManagerAsync`; при исключении `TorrentApiService.cs:83-91/137-145` удаляет строку, но не manager.

То же в magnet-пути (`TorrentEngineService.cs:108-110`). Следствие: повторный Add невозможен — строки нет, а engine отвечает «уже зарегистрирован» → `AlreadyExists`.

**Фикс:** откатывать `Track`/удалять из engine при ошибке старта.

**Acceptance:** после неудачного Add нет ни строки, ни manager; повторный Add работает.

### TOR-6 · Medium · Torrent — `UserDeletedConsumer` теряет retry-состояние [S]

**Где:** `Backend/BarkCloud.Torrent/Consumers/UserDeletedConsumer.cs:37-43` — ошибка `Directory.Delete` логируется и проглатывается, затем строки удаляются.

**Почему проблема:** данные удалённого аккаунта остаются на диске без пути повторной очистки.

**Фикс:** при ошибке удаления — бросать (и включить retry, см. MQ-1) или сохранять durable cleanup-маркер.

**Acceptance:** временный отказ ФС → повтор после восстановления удаляет каталог.

### TOR-7 · Low–Medium (perf) · Torrent — тяжёлый progress stream [S]

**Где:** `Host/TorrentApiService.cs:486-500` — каждые 1500 мс `ListByUser`; `Persistence/TorrentStore.cs:13-17` — `Include(Files)`, tracked, с `byte[] TorrentFile`.

Число стримов на пользователя не ограничено; тот же запрос — в `ListTorrents` (`:151`).

**Фикс:** отдельная `AsNoTracking`-проекция только нужных полей (и для `ListTorrents`).

**Acceptance:** SQL snapshot не выбирает `TorrentFile`.

---

## Medium–Low / Low

### TOR-8 · Low · Torrent — дубли приоритетов [S]
`Host/TorrentApiService.cs:403-431` — параллельные `SetFilePriority` для одного индекса создают несколько строк; в `Persistence/TorrentContext.cs:17-29` нет уникального `(TorrentId, Index)`. Restart (`TorrentStartupService.cs:67-75`) применяет их в произвольном порядке.
Касается только magnet-торрентов: для `.torrent` строки создаются при добавлении (`:118-128`).
**Фикс:** уникальный индекс + upsert.

### TOR-9 · Low–Medium · Torrent — общий engine и дубликаты между пользователями [S]
Engine один на процесс, а проверка дубликата — по пользователю (`Persistence/TorrentStore.cs:55-56`). Если torrent с этим infohash уже есть у пользователя A, пользователь B получает `AlreadyExists` с текстом «уже зарегистрирован в движке» (`DuplicateTorrentException.cs:6`, `TorrentApiService.cs:83-86,137-140`). Это функциональный отказ и утечка факта, что такой torrent есть у кого-то.
**Фикс:** общий manager на infohash с учётом владельцев (refcount) либо отдельные менеджеры/каталоги на пользователя; ответ не должен раскрывать чужие данные.

### WEB-4 · Medium–Low · Web — admin-cookie не привязана к пользователю [R + S+]
`Backend/BarkCloud.Web/Auth/AdminGate.cs:37-81` — подпись `HMAC("admin:{exp}")` без userId/sid; `Auth/AuthGateway.cs:203-219` (`LogoutAsync`) не вызывает `Lock`. Анонимного доступа нет: admin-endpoint'ы требуют и обычную сессию.
**Почему проблема:** на общем браузере следующий вошедший в течение 30 мин получает админ-права. При XSS (WEB-1) у админа — admin-операции.
**Фикс:** `HMAC("admin:{userId}:{sid}:{exp}")` со сверкой с текущим пользователем; `Lock` при logout.
**Acceptance:** cookie A не открывает gate B; после logout `IsUnlocked=false`.

### IDN-7 · Medium–Low · Identity — регистрационные коды после подтверждения [S+]
`Features/ConfirmAccount/ConfirmAccountCommandHandler.cs:111-146` гасит только использованный код; остальные коды владельца живут до 6 ч (`CreateAccountCommandHandler.cs:140`) и выдают refresh без пароля/2FA. `Users/Features/ConfirmUser` подтверждает и уже подтверждённого.
Предусловие: знать значение старого кода или угадать (5 попыток на код). Это не generic takeover.
**Фикс:** при подтверждении удалять все `Registration`-коды владельца; `ConfirmUser` отказывает для не-draft.
**Acceptance:** второй код после подтверждения → `ConfirmationCodeNotFound`.

### IDN-8 · Medium–Low · Users/Android — `DeleteDevice` не отзывает сессию [S]
`Backend/BarkCloud.Users/Host/UsersApiService.cs:186` → `Persistence/Services/DevicesStorage.cs:71-79` удаляет только запись. Android вызывает его как «удалить устройство»: `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/ui/settings/DevicesViewModel.kt:63` → `.../data/users/UserRepository.kt:108-109`. Ограничение задокументировано в `Obsidian/BarkCloudVault/Api/UsersClientGuide.md:30`, но клиент его нарушает.
**Фикс:** Android → `IdentityApi.RemoveActiveSession(device_id)` (такого вызова в Android-клиенте пока нет — новая интеграция); backend — депрекейт или вызов `RemoveActiveSessionServer`. В Mac есть неиспользуемый `deleteDevice` (`Mac/BarkCloudKit/Sources/BarkCloudKit/Data/Users/UserRepository.swift:108`).

### GRP-1 · Low–Medium · GrpcServer — перехватчик ломает коды `RpcException` [S+]
`Backend/BarkCloud.GrpcServer/ServerExceptionInterceptor.cs:62-81` — `catch (Exception)` ловит и `RpcException`, и `OperationCanceledException` → `Status(Unknown, ex.Message)`.
- Затронуты unary-методы Torrent (`TorrentApiService.cs` — `AlreadyExists`, `InvalidArgument`, `NotFound`, `FailedPrecondition`, `Unauthenticated`): клиент получает `Unknown` вместо 400/401/404.
- `ex.Message` произвольных исключений (EF/Npgsql) уходит клиенту.

**Фикс:** `catch (RpcException) { throw; }` первым; отмена клиентом → `Cancelled`; для `Unknown` — общий текст.
**Acceptance:** unit: `RpcException(AlreadyExists)` проходит без изменений; `InvalidOperationException("secret")` → `Unknown` без `"secret"`.

### IDN-9 · Low · Identity — OTP-письма в обход outbox [S+]
`Features/Auth/AuthCommandHandler.cs:172-199`, `ResetPasswordCommandHandler.cs:198-239`, `CreateAccount:168`, `EnableOtpVerification:207` — код фиксируется в БД, затем прямой `Publish` (`Infrastructure/NotificationQueueSender.cs:32`).
**Почему проблема:** при недоступном брокере повтор в окне cooldown письма не шлёт («код в силе») — минуты (для reset до 1 ч) без возможности войти/сбросить.
**Фикс:** использовать существующий `NotificationOutbox` в транзакции challenge.

### WEB-5 · Low · Web — валидатор icon-прокси [R + S+]
`Backend/BarkCloud.Web/WebEndpoints.cs:471-497` — `PublicHost ?? Request.Host.Host`, сравнивается только host (порт игнорируется). `HttpClient "files-upload"` (`Program.cs:89`) следует редиректам.
**Почему проблема:**
- при пустом `App:PublicHost` — SSRF по заголовку `Host`; генератор compose всегда задаёт `App__PublicHost`, поэтому условно;
- blind-запросы на любой порт публичного домена по пути `/download/`.

**Фикс:** сравнивать `scheme+host+port` с явным Files base; без fallback на `Request.Host`; `AllowAutoRedirect=false`.

### GRP-2 · Low (латентный) · Shared.Exceptions — кэш экземпляров исключений [S+]
`Shared/BarkCloud.Shared.Exceptions/Interceptors/ExceptionClientInterceptor.cs:11,36-47,57-67` — статический список экземпляров, один объект бросается во всех потоках; `ErrorMetadata` (например retry-after у `TooManyRequestsException`) теряется; ленивая инициализация не потокобезопасна.
**Фикс:** новый экземпляр на вызов + перенос trailers.

### Мелкие Low (Identity/Users) [S]
- **IDN-L1.** IP в письмах безопасности и геолокации — из клиентского `x-ip-address`/`X-Forwarded-For` (`GrpcServer/Tracker/RequestContextInterceptor.cs:42,74,83`); геолокация по открытому HTTP на `ip-api.com` (`Identity/Infrastructure/LocationClient.cs:9,34`), IP не экранирован.
  **Фикс:** брать `SourceIp`; `IPAddress.TryParse` + `Uri.EscapeDataString`; HTTPS/локальная GeoIP. **Не** ограничивать `x-ip-address` service-токеном: Web ходит в Identity без service-токена и сам ставит `x-ip-address`/`x-real-ip` (`Backend/BarkCloud.Web/Infrastructure/DeviceInfo.cs:39,44`), такое правило сломает IP для Web. Значения в письмах HTML-экранируются; эффект — подмена IP/геолокации в письме.
- **IDN-L2.** Нет уникальных индексов `UserPasswords.UserId`, `AuthUserProperties.UserId` (`Persistence/Contexts/IdentityContext.cs:35-59`): гонка создаёт дубли → смена пароля может обновить не ту строку.
  **Фикс:** миграция с дедупликацией + unique.
- **IDN-L3.** Повтор TOTP-кода в окне (`timeStepMatched` не сохраняется): `AuthCommandHandler.cs:224`, `ConfirmResetPasswordCommandHandler.cs:147`, `DisableOtpVerificationCommandHandler.cs:89`, `EnableOtpVerificationCommandHandler.cs:124`, `ConfirmOtpVerificationCommandHandler.cs:88`.
  **Фикс:** `LastAcceptedTotpStep`.
- **IDN-L4.** Нет серверной политики паролей: пустой/1 символ/>72 байт BCrypt (`SetPasswordCommandHandler.cs:82`, `ForceSetPasswordServer:38`, `ConfirmResetPassword:180-182`). Web проверяет минимум 6 символов только при регистрации и сбросе (`RegistrationGateway.cs:59`, `PasswordResetGateway.cs:82`); `/security/password` передаёт пароль без проверки (`SettingsEndpoints.cs:240-244`).
- **IDN-L5.** `GetUser` отдаёт любой профиль по id, включая черновики и `storage_limit_gb`; `ProfileVisibility` игнорируется (`Users/Features/GetUser/GetUserQueryHandler.cs:29-54`, `UserMapping.cs:20`).
- **IDN-L6.** Device id (при коллизии PK ошибку проглатывает `SessionIssuer.cs:143-147`; cookie `bark_did` не удаляется при logout — `AuthGateway.cs:223-227`, поэтому второй аккаунт в том же браузере, вероятно, не зарегистрирует устройство): `ConfirmAccount`/no-email `CreateAccount` используют `DeviceId ?? DeviceName` и не регистрируют устройство (`ConfirmAccountCommandHandler.cs:137`, `CreateAccountCommandHandler.cs:106`). `UserDevice.Id` — глобальный PK, выбранный клиентом (`Users/Domain/UserDevice.cs:7`).
- **IDN-L7.** Некорректный ввод → `Unknown`: `Guid.Parse`/`Convert.FromBase64String`/NRE (`RequestContextInterceptor.cs:123`, `IdentityApiService.cs:189`, `UsersApiService` device-методы, `ConfirmOtpVerificationCommandHandler.cs:64`).
  **Фикс:** `TryParse` → `InvalidArgument` (вместе с GRP-1).
- **IDN-L8** (по сути Info, только с токеном владельца). `DisableOtpVerification` с неизвестным типом шлёт письмо «2FA отключена», ничего не отключив (`DisableOtpVerificationCommandHandler.cs:69,107,141-155`).
- **IDN-L9.** Refresh хранится открытым текстом, 9999 дней, без ротации (`Persistence/Services/RefreshTokensStorage.cs:16,28,30`); нет индексов под горячие запросы; `WebAuthnChallenges` удаляются только при использовании (`WebAuthnStorage.cs:113`), просроченные не чистятся (создание ограничено по IP).
- **IDN-L10.** `RegistrationPolicy.cs:39` тянет весь конфиг Identity на каждый анонимный `CreateAccount`/`ConfirmAccount` до rate-limit.
  **Фикс:** кэш 10–30 с, лимит раньше.
- **IDN-L11.** Повторный `CreateAccount` с чужим email перезаписывает username/имя черновика; черновики занимают username (`CreateAccountCommandHandler.cs:85-89`, `Backend/BarkCloud.Users/Features/OverrideDraftUser/OverrideDraftUserCommandHandler.cs`).
- **IDN-L12.** `UserDeletedConsumer` Identity не удаляет `WebAuthnCredentials`, `WebAuthnChallenges`, `PendingNotifications` (`Consumers/UserDeletedConsumer.cs:37-43`). Существование пользователя не проверяют ни `SessionIssuer` (`:69-90`), ни `CreateToken` (`:19-36`).
  Вход по passkey после **успешной** очистки, скорее всего, блокируется: owner-callback (`CompleteWebAuthnAssertionCommandHandler.cs:81-91`) резолвит user handle через `AuthUserProperties.WebAuthnUserHandle` (`WebAuthnStorage.cs:80-83`), а consumer эти строки удаляет → `false`. Не проверено [H]: поведение Fido2 при пустом user handle (non-discoverable ключ). Реальный риск возникает в связке с MQ-1: если consumer упал, всё (refresh, ключи) остаётся рабочим.
  **Фикс:** удалять эти таблицы в consumer + tombstone/проверка существования при выдаче сессии (общая с MQ-1).

### WEB-6 · Info · Web
`Auth/AdminGate.cs:66-67` — `FromUnixTimeSeconds(long.MaxValue)` → `ArgumentOutOfRangeException` = 500 только на собственный запрос. Фикс: проверка диапазона → `false`.

### Info · Notification
`Parsers/HtmlEmailTemplateParser.cs:30` берёт шаблоны из `Environment.CurrentDirectory`, а не `AppContext.BaseDirectory`. Запуск не из каталога приложения ломает отправку. В контейнере работает, если `WORKDIR` = каталог приложения.

---

## Проверено, замечаний нет

- **JWT:** подпись/issuer/audience/lifetime, `ClockSkew=0`, минимальная длина ключа, токен только из `x-auth-token`.
- **Отзыв сессий:** снимок до старта Kestrel, дельта + полный ресинк; ownership в `RemoveActiveSession`/`Logout`.
- **Email-OTP и reset-коды:** `FixedTimeEquals`, 5 попыток, атомарное погашение. **WebAuthn assertion:** проверка владельца, CAS по счётчику.
- **Users:** device-операции по `UserId` из токена; `SearchUsers` исключает черновики; уникальность username/email.
- **Files:** User/Service policy, owner/grant-проверки в cloud/share/trash/album; публичный `download/{id}` отдаёт оригинал только для avatar/preview, остальное — по временному capability; квота resumable под транзакционной блокировкой; FFmpeg/ffprobe с таймаутами, без интерполяции имён в shell.
- **Torrent:** все операции ограничены `UserId` (`RequireOwned`); HTTP import ходит только во внутренний Files.
- **Configuration:**
  - `ConfigurationAccessInterceptor` с `FixedTimeEquals`; ключ обязателен вне Development (`ConfigurationAccessPolicy.cs`);
  - все RPC unary — гейт их покрывает;
  - в nginx не проксируется.
- **Notification:** все значения payload экранируются `WebUtility.HtmlEncode` (`HtmlEmailTemplateParser.cs:39-45`); адреса в логах маскируются.
- **`SourceIpResolver`:** не доверяет клиентским заголовкам для лимитов (при условии, что сервисы недоступны в обход nginx — в compose порты не публикуются).

Не просматривались или только бегло: `NotificationOutboxWorker`, `UpdatePrivacySettings`, `ListOtpVerification`, `ListWebAuthnCredentials`, `ChangeName/ChangeBio`, `UpdateStorageLimit`, `SecurityUtilities`, клиентская часть `ClientApp` (кроме связи с WEB-1).

## Не дефекты продукта (состояние тестов)

| Проект | Итог | Причина падений/пропусков |
|---|---|---|
| Identity.Tests | 349/349 | — (но моки FindByLogin не соответствуют контракту, см. IDN-3) |
| Users.Tests | 81 ok, 7 skip | skip — нет `BARKCLOUD_TEST_POSTGRES` |
| GrpcServer.Tests | 67/67 | — |
| Configuration.Tests | 77/77 | — |
| Torrent.Tests | 86 ok, 1 skip | долгий multipart-тест |
| Files.Tests | 511 ok, 112 skip | skip — нет Postgres |
| Web.Tests | 206 ok, **1 fail**, 3 skip | `ComposeImageServiceTests.TryRewrite_PreservesCrlfAndTrailingContent`: **fixture** на Windows — raw-string уже с CRLF, `Replace("\n","\r\n")` даёт `\r\r\n`. Фикс теста: сначала нормализовать к LF; добавить хвостовой sentinel |
| Notification.Tests | 20 ok, **7 fail** | fake SMTP (`Tests/Backend/BarkCloud.Notification.Tests/Senders/LocalSmtpServer.cs:40-66`) использует ephemeral RSA-ключ — Windows SSPI не поддерживает (`0x8009030E`). Фикс fixture: `X509CertificateLoader.LoadPkcs12(..., UserKeySet)`. Тесты «отклонение недоверенного сертификата» сейчас могут проходить из-за EOF, а не проверки trust — после фикса fixture перепроверить |
| Identity/Users.IntegrationTests | 0/66, 0/28 | нет `BARKCLOUD_TEST_POSTGRES` (Docker недоступен) |
| Shared.Identity/Queue.Tests | 1/1 | заглушки `true.Should().BeTrue()` — покрытия нет |

## Приоритет исправлений

1. **WEB-1** (SVG XSS) и **TOR-1** (torrent path traversal) — до релиза. WEB-1 заодно гасит сцепки с IDN-2/WEB-4/IDN-5.
2. **IDN-2** (step-up на passkey), **IDN-1** (TOTP-счётчик на аккаунт), **IDN-4** (отзыв сессий при смене пароля).
3. **MQ-1** (retry консьюмеров; вместе с **TOR-6** и IDN-L12).
4. Доступность: **FIL-1**, **WEB-2**, **WEB-3**, **FIL-3**, **IDN-6**.
5. **PLT-1** (nginx allowlist + reflection), **CFG-1**, **FIL-2**, **FIL-4**.
6. Остальное: IDN-3, TOR-2…TOR-9, WEB-4, WEB-5, IDN-5, IDN-7, IDN-8, IDN-9, GRP-1, GRP-2, Low, тестовые fixture.

## Ограничения аудита

- Сквозные сценарии с настоящими Postgres/RabbitMQ/S3 не запускались (Docker недоступен). Fault-injection для MQ-1/FIL-2/CFG-1 не выполнялся.
- Сетевой SSRF не выполнялся; запросов к production не было.
- Находки [S] перепроверены вторым проходом только статически, динамически не воспроизводились.
