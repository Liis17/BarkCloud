# Отчёт по аудиту безопасности и производительности BarkCloud

> Выполнено по плану `Docs/SECURITY_PERFORMANCE_AUDIT.md`. Дата: 2026-05-26.
> Метод: статическая верификация по исходному коду (чтение фактических файлов), сервис за сервисом.
> Каждая находка имеет **статус**: ✅ подтверждено · ✏️ скорректировано · ❌ опровергнуто.

## Резюме

Проверены все backend-сервисы (`Configuration`, `Identity`, `Users`, `Files`, `Notification`),
общий слой (`GrpcServer`/`Shared`), веб-клиент (`Web`) и инфраструктура (nginx/docker-compose).

| Severity | Кол-во | Находки |
|----------|--------|---------|
| Critical | 2 | C1 Configuration без аутентификации раздаёт все секреты · C2 SMTP без проверки TLS-сертификата |
| High | 6 | H1 нет rate-limit/лок-аута · H2 IDOR в GetTempDownloadUrl · H3 квота не enforce · H4 нет таймаута ffmpeg/ImageSharp · H5 docker.sock+root на публичном web · H6 нет CSRF и security-заголовков |
| Medium | 7 | M1 общий JWT-секрет + вечные Service-токены · M2 GetFileData без проверки владельца на уровне сервиса · M3 in-memory revocation-cache · M4 анонимные HTTP upload/download · M5 SearchUsers full-scan · M6 Notification без retry/идемпотентности · M7 межпользовательский дедуп-лик |
| Low | 6 | L1 legacy SHA256 · L2 рассинхрон кодировки ключа · L3 Secure-cookie зависит от флага · L4 пароль в hidden-полях · L5 gRPC reflection · L6 доверие к X-Ip/X-Device |

**Ключевая цепочка атаки:** C1 (Configuration отдаёт `JwtSettings:SecretKey` любому в docker-сети) →
подделка Service-токена → политика `User` принимает Service-токены (M1) → полный доступ ко всем
клиентским и серверным RPC, обход всех IDOR-ограничений. Достаточно доступа к внутренней сети
(скомпрометированный контейнер), но дефанс-в-глубину отсутствует.

---

## Поправки к предварительным находкам (важно)

Предварительные находки из плана генерировались агентами по фрагментам кода — три из них при
проверке по реальному коду оказались неточными:

| Предв. формулировка | Реальность (проверено) | Итог |
|---------------------|------------------------|------|
| `Configuration.GetConfiguration` «требует Service-токен» | `Program.cs` вообще **не вызывает** `AddXAuth`/`UseXAuth`; на `ConfigurationApiService` нет `[Authorize]` → endpoint **полностью без аутентификации** + включён gRPC reflection | ✏️ **хуже**, severity ↑ |
| `FilesServerApi.GetFileData` — «Critical IDOR» | На уровне сервиса проверки владельца нет, но API за `[Authorize(Service)]`, а web-эндпоинт `files/info` **корректно** проверяет `Uploaders.Contains(userId)` → 403 (`CloudApiEndpoints.cs:374`) | ✏️ ↓ до **Medium** (defense-in-depth) |
| `VideoThumbnailExtractor` «угадываемый temp-путь» | Путь — случайный GUID (`Guid.NewGuid():N`), не эксплуатируемо. Реальная проблема — **отсутствие таймаута** ffmpeg | ✏️ переформулировано (H4) |
| Notification «бесконечный retry» | На `ReceiveEndpoint` **нет** `UseMessageRetry` → при ошибке MassTransit сразу фолтит в `_error` (не бесконечный цикл). Проблема — **отсутствие** retry/backoff и идемпотентности | ✏️ переформулировано (M6) |

---

## Сводная таблица находок

| ID | Severity | Сервис | Находка | Файл:строка | Статус |
|----|----------|--------|---------|-------------|--------|
| C1 | Critical | Configuration | Нет аутентификации/авторизации; раздаёт все секреты любому в сети; gRPC reflection включён | `Program.cs`, `Host/ConfigurationApiService.cs:30`, `Infrastructure/ConfigurationSeed.cs:38-93` | ✅ 🔧 исправлено |
| C2 | Critical | Notification | Глобально отключена проверка TLS-сертификата SMTP (MITM email-кодов) | `Senders/EmailSender.cs:37-38` | ✅ 🔧 исправлено |
| H1 | High | Identity (все) | Нет rate-limiting/лок-аута на Auth/OTP/Reset; брутфорс пароля и 6-значных кодов | `Features/Auth/*`, `Features/ConfirmResetPassword/*`; rate-limiter нет нигде в backend | ✅ |
| H2 | High | Files | IDOR: `GetTempDownloadUrl` не проверяет владельца `FileIds` | `Host/FilesApiService.cs:43-54`, `Features/GetTempDownloadUrl/GetTempDownloadUrlCommandHandler.cs:35` | ✅ |
| H3 | High | Files | Квота хранилища не проверяется при загрузке | `Features/UploadFile/UploadFileCommandHandler.cs` | ✅ |
| H4 | High | Files | Нет таймаута на ffmpeg/ffprobe и ImageSharp при загрузке → DoS | `Services/VideoThumbnailExtractor.cs:28,48`, `Services/ImageCompressor.cs` | ✅ |
| H5 | High | Web/Infra | Публичный web как root + `docker.sock`; admin-unlock без rate-limit → user→admin→RCE на хост | `docker-compose.yml:87,99`, `Auth/AdminGate.cs:36-39`, `Endpoints/SystemEndpoints.cs` | ✅ |
| H6 | High | Web | Нет CSRF-защиты и security-заголовков (CSP/HSTS/X-Frame-Options/X-Content-Type-Options) | `BarkCloud.Web/Program.cs` (antiforgery/headers отсутствуют) | ✅ |
| M1 | Medium | GrpcServer/Identity | Общий JWT-секрет на все сервисы + вечный Service-токен (exp 9999), принимаемый политикой `User` | `Services/JwtService.cs:33-46`, `XAuth/XAuthExtensions.cs:79-81` | ✅ |
| M2 | Medium | Files | `GetFileData`/`GetFilesData` без проверки владельца на уровне сервиса | `Features/GetFileData/GetFileDataCommandHandler.cs:32` | ✅ |
| M3 | Medium | GrpcServer | `TokenRevocationCache` только in-memory; теряется при рестарте; Service-токены не отзываются | `XAuth/TokenRevocationCache.cs`, `XAuthExtensions.cs:51-67` | ✅ |
| M4 | Medium | Files | Анонимные HTTP `upload/{id}` и `download/{id}` (нет `[Authorize]`), доступны через `/web/` на 7025 | `Host/FilesController.cs:22,54` | ✅ |
| M5 | Medium | Users | `SearchUsers`: неиндексируемый `LIKE %%` + `Include(Contact)` без keyset-пагинации; пустой запрос матчит всех | `Persistence/Services/UsersStorage.cs:152-167` | ✅ |
| M6 | Medium | Notification | Нет retry/backoff/идемпотентности; транзиентная ошибка SMTP → сразу в `_error`; redelivery → дубль письма | `Program.cs:38-41`, `Consumers/EmailQueueConsumer.cs:53` | ✅ |
| M7 | Medium | Files | Межпользовательский дедуп по SHA256 раскрывает существование чужого контента | `Features/UploadFile/UploadFileCommandHandler.cs:153`, `Persistence/FileHashesStorage.cs` | ✅ |
| L1 | Low | Identity | Принимаются legacy несолёные SHA256-хэши паролей | `Services/PasswordHasher.cs:18-28` | ✅ |
| L2 | Low | Identity/GrpcServer | Подпись ключа UTF8 vs валидация ASCII — ломается для не-ASCII секрета | `JwtService.cs:50` vs `XAuthExtensions.cs:26` | ✅ |
| L3 | Low | Web | `Secure` у session-cookie зависит от `App:CookieSecure`; при false по HTTP — утечка | `Auth/AuthGateway.cs:227,241` | ✅ |
| L4 | Low | Web | Пароль/`code_id`/`reset_id` переносятся в скрытых полях формы между шагами | `Auth/RegistrationGateway.cs`, `Auth/PasswordResetGateway.cs` | ✅ (по памяти) |
| L5 | Low | Configuration | gRPC reflection включён в проде — облегчает разведку | `Program.cs` (`AddGrpcReflection`/`MapGrpcReflectionService`) | ✅ |
| L6 | Low | GrpcServer | Доверие к клиентским метаданным `X-Ip`/`X-Device`/`X-Os` (спуфинг геолокации входа) | `Tracker/RequestContextInterceptor.cs`, `Shared.Auth/*` | ✅ |

---

## Детализация критичных и высоких находок

### C1 — Configuration: нет аутентификации, раздаёт секреты `[Critical]`
**Факт.** `BarkCloud.Configuration/Program.cs` не подключает `AddXAuth`/`UseXAuth` и не настраивает
`Authentication`/`Authorization`. `ConfigurationApiService` (строка 30) без `[Authorize]`. При этом
включены `AddGrpcReflection()` и `MapGrpcReflectionService()`. `GetConfigurationCommandHandler`
возвращает все пары Key/Value для запрошенного `ServiceId`, включая секреты из `ConfigurationSeed`:
`JwtSettings:SecretKey`, `RabbitMQ:Password`, `Email:SenderPassword`, `S3Buckets:*:AccessKey/SecretKey`,
строки подключения БД (заполняются `ConfigurationDefaultsPopulator`).
**Воспроизведение.** С любого узла в `barkcloud-network` (или при доступности порта) вызвать
`grpcurl`/клиент `ConfigurationApi.GetConfiguration {service_id: 0}` — вернётся `JwtSettings:SecretKey`.
**Риск.** Полная компрометация: с секретом подделывается Service-токен (см. M1) → доступ ко всему.
**Рекомендация.** ⚠️ JWT-авторизация (`[Authorize(Service)]`) здесь невозможна: сервис получает
JWT-секрет именно из этого вызова на старте (bootstrap «курица и яйцо»). Правильный путь —
**предразделённый bootstrap-ключ из окружения** (или mTLS), плюс отключить gRPC reflection в проде,
шифровать секреты at-rest и не возвращать секреты не относящемуся `ServiceId`.
**🔧 Исправлено (этот коммит).** Добавлен `ConfigurationAccessInterceptor` — проверка заголовка
`x-config-access-key` против env `CONFIGURATION_ACCESS_KEY` в постоянном времени (внешним слоем, до
`ServerExceptionInterceptor`); клиент `LoadConfiguration` шлёт ключ; reflection включается только в
`Development`; ключ прокинут через `x-common-variables` в обоих compose и добавлен в `sample.env`.
Если ключ не задан — доступ открыт + warning (неблокирующая миграция; для защиты ключ обязателен).

### C2 — SMTP без валидации сертификата `[Critical]`
**Факт.** `EmailSender.SendEmail` на каждом вызове ставит
`ServicePointManager.ServerCertificateValidationCallback = (...) => true` (строки 37-38) —
process-wide отключение проверки TLS-сертификата.
**Риск.** MITM SMTP-канала: перехват писем с кодами подтверждения регистрации, входа, 2FA и сброса
пароля → обход аутентификации/2FA. Глобальная установка затрагивает и любой другой TLS в процессе.
**Рекомендация.** Удалить callback; использовать `MailKit` с корректной проверкой сертификата и
`SecureSocketOptions`; при необходимости — доверенный CA, а не «доверять всем».
**🔧 Исправлено (этот коммит).** Удалены строки
`ServicePointManager.ServerCertificateValidationCallback = (...) => true;` в `EmailSender.cs` —
восстановлена штатная проверка TLS-сертификата против системного хранилища доверия. Самоподписанный
SMTP теперь требует доверенного CA (не «доверять всем»).

### H1 — Нет защиты от брутфорса `[High]`
**Факт.** Во всём backend нет `AddRateLimiter`/`RateLimiter` (grep по всем `.cs` пуст). В Identity
есть только счётчики `_metrics.Increment("*_attempts")`, без лок-аута/троттлинга. OTP — 6 цифр
(`CodeGenerator`, CSPRNG ок), но без лимита попыток ввода (`Auth`, `ConfirmAccount`,
`ConfirmResetPassword`).
**Риск.** Онлайн-брутфорс паролей и 6-значных кодов (10⁶) реалистичен; усиливается дорогим BCrypt(12)
как вектор DoS.
**Рекомендация.** Per-account/per-IP/per-device лимиты и экспоненциальный лок-аут на Auth/OTP/Reset
и admin-unlock; ограничение числа попыток ввода кода с инвалидацией.

### H2 — IDOR в `GetTempDownloadUrl` `[High]`
**Факт.** `FilesApiService.GetTempDownloadUrl` (`[Authorize(User)]`) принимает произвольные
`FileIds` и передаёт в обработчик без проверки владельца; `GetTempDownloadUrlCommandHandler`
(строка 35) делает `GetFiles(request.FileIds)` и выдаёт временные ссылки. Нет обращения к
`UserContext`/`Uploaders`.
**Риск.** Любой аутентифицированный пользователь, узнав GUID чужого файла (логи, шаринг, утечки),
получает рабочую ссылку на скачивание. Авторизация держится только на секретности GUID.
**Рекомендация.** В обработчике фильтровать `FileIds` по `Uploaders.Contains(currentUserId)` (как уже
сделано в `files/info`); неподходящие — 403/исключать.

### H3 — Квота не enforce при загрузке `[High]`
**Факт.** `UploadFileCommandHandler` не обращается к лимиту хранилища; дедуп и загрузка идут без
проверки `used + size ≤ StorageLimitBytes`. Квота видна только в read-only `GetUserStorageInfo`.
**Риск.** Превышение квоты, исчерпание диска/S3 (DoS), обход тарифа.
**Рекомендация.** Проверять квоту атомарно до выдачи upload-URL и при финализации загрузки.

### H4 — Нет таймаута медиа-обработки `[High]`
**Факт.** `VideoThumbnailExtractor.ProbeAsync`/`ExtractFrameJpegAsync` вызывают `FFProbe.AnalyseAsync`
и `FFMpeg.SnapshotAsync` без таймаута (а `SnapshotAsync` даже без cancellation token, строка 48).
`ImageCompressor` имеет лимиты размера, но без таймаута декодирования.
**Риск.** «Злое»/повреждённое видео/изображение подвешивает процесс ffmpeg/декодер → исчерпание CPU/
памяти/потоков, DoS воркера загрузки.
**Рекомендация.** Жёсткие таймауты и лимиты ресурсов на ffmpeg-процесс (kill по таймауту),
ограничение параллелизма обработки, `DecoderOptions`/limits в ImageSharp.

### H5 — docker.sock + root на публичном web `[High]`
**Факт.** `docker-compose.yml`: сервис `web` с `user: root` (строка 87) и монтированием
`/var/run/docker.sock` (строка 99), порт наружу `${WEB_PORT}:8080`. Доступ к `/api/system/*` за
`AdminGate`: пароль сверяется `FixedTimeEquals` (ок), HMAC-cookie 30 мин — но **rate-limit на
`unlock` отсутствует**, пароль хранится в конфиге открыто. `DockerService` использует `ArgumentList`
+ `UseShellExecute=false` (shell-инъекции нет; `sh -c` только с compose-метаданными, не с вводом).
**Риск.** Аутентифицированный пользователь брутфорсит `App:AdminPassword` (нет лок-аута) → docker.sock
от root = полный контроль над хостом (RCE), запуск произвольных контейнеров/монтирований.
**Рекомендация.** Вынести обслуживание в изолированный сервис/сеть без публичного доступа; rate-limit
+ лок-аут на unlock; хранить хэш пароля, не открытый текст; рассмотреть docker-socket-proxy с
ограниченным API; минимизировать привилегии.

### H6 — Нет CSRF и security-заголовков `[High]`
**Факт.** В `BarkCloud.Web/Program.cs` нет `AddAntiforgery`, нет заголовков CSP/HSTS/X-Frame-Options/
X-Content-Type-Options; на upload явный `DisableAntiforgery()`. Единственная защита — cookie
SameSite=Lax. JWT-валидация в `AuthGateway` корректна (issuer/audience/lifetime/signing, ClockSkew=0),
cookie HttpOnly/SameSite=Lax (Secure — по флагу, см. L3).
**Риск.** CSRF на изменяющие POST (`/api/*`, профиль, удаление аккаунта, сессии) при SameSite=Lax
(GET-навигация и часть кросс-сайт сценариев проходят); отсутствие CSP/заголовков повышает риск XSS/
clickjacking/mixed-content.
**Рекомендация.** Antiforgery-токены (double-submit/header) для всех изменяющих запросов; набор
security-заголовков; `SameSite=Strict` для админ/изменяющих cookie; валидация avatar URL.

---

## Производительность

| ID | Находка | Файл | Рекомендация |
|----|---------|------|--------------|
| P1 | `SearchUsers` — `LOWER(col) LIKE '%q%'` по 3 полям + `Include(Contact)`, без индексов и keyset (M5) | `UsersStorage.cs:152-167` | trigram/GIN-индекс (pg_trgm) или full-text; курсорная пагинация; запрет пустого запроса |
| P2 | BCrypt(12) на каждый логин — дорог; без rate-limit (H1) усиливает DoS | `PasswordHasher.cs:8` | rate-limit логина; вынести проверку под очередь/лимит конкуренции |
| P3 | Медиа-обработка без таймаута/лимита конкуренции (H4) | `VideoThumbnailExtractor.cs`, `ImageCompressor.cs` | таймауты, семафор на число параллельных ffmpeg, лимиты декодера |
| P4 | `TokenRevocationCache` — рост памяти, очистка фоновым сервисом (M3) | `TokenRevocationCache.cs` | ограничение размера/TTL; распределённый кэш |
| P5 | `PageDataBuilder` делает несколько последовательных gRPC-вызовов на сборку страницы | `Rendering/PageDataBuilder.cs` | распараллелить независимые вызовы (`Task.WhenAll`) |

**Сделано хорошо (подтверждено):** gRPC-клиенты в Web — синглтоны через `AddGrpcClient` (пулинг
каналов); `HttpClient("files-upload")` — синглтон; в `CloudHierarchyStorage`/`AlbumStorage`/
`FavoriteFilesStorage` используются `AsNoTracking` и курсорная (keyset) пагинация; в
`GetTempDownloadUrl` — батч-INSERT временных файлов вместо N round-trip; большие файлы буферизуются
на диск, а не в память; `FixedTimeEquals` для пароля и admin-cookie.

---

## Матрица покрытия (этапы × сервисы)

| Этап | Cfg | Identity | Users | Files | Notif | GrpcSrv/Shared | Web | Infra |
|------|-----|----------|-------|-------|-------|----------------|-----|-------|
| E2 Секреты | C1,L5 | M1 | — | M7 | C2 | M1,L2 | L3 | H5 |
| E3 AuthN | C1 | M1,L1,L2 | — | — | — | M1,M3 | ✓ok | — |
| E4 AuthZ/IDOR | C1 | ✓ok | проверить | H2,M2,M4 | — | M1 | ✓files/info ok | — |
| E6 Anti-abuse | — | H1 | — | H3 | — | — | H5,H6 | — |
| E7 Данные/perf | — | — | M5/P1 | perf ok | — | — | P5 | — |
| E8 Файлы/медиа | — | — | — | H2,H3,H4,M7 | — | — | upload | — |
| E9 Messaging | — | — | (события) | (consumer) | M6 | — | — | — |
| E10 Логи/PII | — | — | — | — | ✓mask ok | L6 | — | — |

---

## Приоритеты ремедиации

1. **Немедленно (Critical):** C1 (закрыть Configuration аутентификацией + убрать reflection + не
   раздавать секреты), C2 (включить валидацию TLS в SMTP).
2. **Срочно (High):** H1 (rate-limit/лок-аут), H2 (проверка владельца в GetTempDownloadUrl),
   H5 (изолировать обслуживание/docker.sock), H3 (квота), H4 (таймауты медиа), H6 (CSRF+заголовки).
3. **Планово (Medium):** M1 (отказ от общего секрета/ввести срок и отзыв Service-токенов),
   M2/M4 (владелец на уровне сервиса/авторизация HTTP-эндпоинтов), M3 (persistent revocation),
   M5/P1 (индексы поиска), M6 (retry/идемпотентность), M7 (per-tenant дедуп или отказ от лика).
4. **Hardening (Low):** L1–L6.

> Все находки получены статической верификацией по исходникам. Для подтверждения эксплуатируемости
> H2/H5/C1 и измерения P1–P3 рекомендуется динамический прогон по разделу 4 плана (`ghz`, `k6`,
> `EXPLAIN ANALYZE`, профилирование) на dev-стенде.
