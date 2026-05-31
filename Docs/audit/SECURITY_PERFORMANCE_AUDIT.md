# Аудит безопасности и производительности BarkCloud

> Пошаговый план проведения аудита backend и web-клиента BarkCloud.
> Версия документа: 1.0 · Дата: 2026-05-26 · Охват: Backend + Web + инфраструктура.

## 0. Введение

### 0.1 Цель
Систематически проверить безопасность и производительность серверной части BarkCloud
(4 gRPC-микросервиса, сервис уведомлений, веб-клиент, общий хост и shared-библиотеки) и
поддерживающей инфраструктуры. Документ — методология «по шагам», по которой аудит можно
выполнить повторяемо, с фиксацией находок в едином формате (раздел 5).

### 0.2 Охват
- **В охвате:** `Configuration`, `Identity`, `Users`, `Files`, `Notification`, `GrpcServer`,
  `Shared.*`, `Web`; инфраструктура — nginx, docker-compose, PostgreSQL, MinIO, RabbitMQ, Seq.
- **Вне охвата:** мобильные клиенты Android/iOS (кроме упоминания trust-all TLS как граничного
  условия для backend), бизнес-логика UI-страниц сверх вопросов XSS/CSRF.

### 0.3 Архитектура и доверительные границы (модель угроз)
- Браузер ↔ `Web`: HTTP/1.1 (через внешний nginx-vhost, вне репозитория).
- Клиенты/`Web` ↔ микросервисы: gRPC поверх TLS на nginx (порты 7020/7021/7025) → внутрь **h2c
  (plaintext)**. Сертификат **self-signed**, клиенты доверяют всем.
- Микросервис ↔ микросервис: gRPC h2c в `barkcloud-network`.
- `Configuration` → все сервисы: раздача конфигурации и **секретов** при старте.
- Единый **общий JWT-секрет** (`ServiceId.Unknown`) у всех сервисов и web → компрометация одного =
  компрометация выпуска/валидации токенов во всём кластере.
- `Web`-контейнер имеет `user: root` + смонтированный `docker.sock` → управление хостовым Docker.

### 0.4 Легенда severity
**Critical** — удалённый доступ к чужим данным/секретам или RCE без сложных условий.
**High** — обход контроля доступа, отказ в обслуживании, брутфорс без ограничений.
**Medium** — ослабленная защита, утечки метаданных, деградация под нагрузкой.
**Low** — устаревшие практики, hardening, дефекты глубокой защиты.

---

## 1. Этап 0 — Подготовка окружения и инструментов

1. Поднять dev-стенд: `cd Backend` → собрать образы из `Dockerfile` → `docker compose -f
   docker-compose-dev.yml up -d` с `.env` (шаблон `sample.env`).
2. Зафиксировать версии: .NET 10, EF Core, Grpc.AspNetCore, MassTransit, SixLabors.ImageSharp,
   FFMpegCore, версии npm-пакетов React/Babel, подгружаемых страницами `Pages/*.html`.
3. Установить инструменты (детали и команды — Приложение B):
   - SAST/зависимости: `dotnet list package --vulnerable`, `trivy`/`grype` по образам, `gitleaks`/
     `trufflehog` по git-истории.
   - DAST/web: OWASP ZAP или Burp Suite.
   - Нагрузка: `ghz` (gRPC), `k6`/`bombardier` (HTTP).
   - Профилирование: `dotnet-trace`, `dotnet-counters`, `dotnet-gc-dump`.
   - БД: `EXPLAIN (ANALYZE, BUFFERS)` в PostgreSQL.
4. Подготовить тестовые аккаунты (минимум два пользователя — для проверок IDOR) и тестовые медиа
   (включая «битые»/огромные изображения и видео для проверки лимитов).
5. Завести журнал находок по шаблону раздела 5.

> **Примечание:** backend-тестов в репозитории нет (есть только example-тесты Android). Часть
> проверок выполняется динамически (gRPC-вызовы `ghz`/`grpcurl`, ручные сценарии) и статически
> (ревью кода по anchors из раздела 3).

---

## 2. Сквозные этапы аудита (E1–E10)

Методология, применяемая к каждому сервису раздела 3. В каждом сервисе указано, какие этапы
релевантны.

- **E1. Инвентаризация и threat model.** Карта сервисов, портов, доверительных границ, потоков
  данных и секретов (DFD); точки входа (клиентские vs серверные RPC, HTTP-эндпоинты).
- **E2. Секреты и конфигурация.** Где и как хранятся JWT-ключ, креды БД/MinIO/RabbitMQ/SMTP; раздача
  через `ConfigurationApi`; `.env`, `appsettings*.json`, `docker-compose*.yml`; секреты в git-истории;
  шифрование at-rest; принцип минимальных привилегий при раздаче.
- **E3. Аутентификация.** JWT (алгоритм, issuer/audience/lifetime/signature/clock-skew); refresh-токены
  (энтропия, ротация, срок); сервисные токены (`TokenType.Service`); валидация на входе (`XAuth`);
  отзыв сессий (`TokenRevocationCache`, переживаемость рестарта).
- **E4. Авторизация / IDOR.** На каждом RPC и endpoint — проверка владения (`OwnerId == caller`,
  `userId ∈ Uploaders`); скоупинг серверных API; состояния гонки (soft-delete/restore).
- **E5. Валидация ввода и доменные ошибки.** Лимиты длины/формата gRPC-полей; многошаговые флоу
  (2FA/регистрация/сброс пароля); перечисление пользователей; маппинг ошибок без утечки внутренних
  деталей (`ServerExceptionInterceptor`).
- **E6. Anti-abuse / rate limiting.** Брутфорс login/OTP/reset/`unlock`; лимиты попыток, лок-аут,
  троттлинг; защита от DoS дорогих операций (медиа-обработка, поиск).
- **E7. Хранилище (EF Core / PostgreSQL).** Параметризация (SQLi), `AsNoTracking` на чтениях, N+1,
  пагинация, индексы под фильтры/поиск, транзакции и атомарность, корректность миграций.
- **E8. Файлы и медиа.** Presigned URL (срок, скоуп владельца), квоты ДО загрузки, лимиты размера/типа,
  decompression-bomb, таймауты ffmpeg/ImageSharp, безопасные временные файлы, межпользовательский
  дедуп-лик.
- **E9. Messaging (RabbitMQ/MassTransit).** Идемпотентность консьюмеров, retry/backoff, dead-letter,
  отравленные сообщения, PII в теле сообщений.
- **E10. Логирование/наблюдаемость и приватность.** Serilog→Seq: маскирование PII/секретов, отсутствие
  токенов/паролей в логах, доступ к Seq, полезность метрик для обнаружения атак.

---

## 3. Аудит по микросервисам (по шагам)

Формат каждого подраздела: **Security (шаги)** → **Performance (шаги)** → **Anchors** (где смотреть).
Severity напротив шага — предварительная оценка по результатам разведки; подтверждается при выполнении.

### 3.1 Configuration — `Backend/BarkCloud.Configuration/`
Релевантные этапы: E1, E2, E4, E7, E10.

**Security:**
1. **[Critical]** Проверить авторизацию `ConfigurationApiService.GetConfiguration`: кто может вызвать
   и **какие значения возвращаются**. Сервис требует токен `TokenType.Service`, но любой сервис (или
   подделанный сервисный токен на общем секрете) может запросить конфигурацию любого `ServiceId` —
   включая секреты (JWT key, RabbitMQ pass, S3 access/secret, SMTP pass). Оценить скоупинг по
   `ServiceId`, минимизацию секретов в ответе, шифрование at-rest, идентификацию вызывающего.
2. **[Medium]** `ConfigurationSeed.cs` / `ConfigurationDefaultsPopulator.cs`: секреты в seed-списке,
   отсутствие шифрования значений в БД; `PopulateDefaultsAsync` не должен затирать секреты.
3. **[Medium]** `UpdateConfiguration` и CRUD `ReservedNames`: авторизация на запись, валидация ключей.

**Performance:**
1. Подтвердить, что сервисы кэшируют конфигурацию на старте и не дёргают `GetConfiguration` на каждый
   запрос. Оценить размер ответа.
2. Индексы под выборку по `(Section, Key, ServiceId)`; единственная миграция — проверить уникальные
   индексы.

**Anchors:** `Host/ConfigurationApiService.cs:30-55`, `Features/GetConfiguration/*`,
`Infrastructure/ConfigurationSeed.cs:18-102`, `Infrastructure/ConfigurationDefaultsPopulator.cs`.

### 3.2 Identity — `Backend/BarkCloud.Identity/`
Релевантные этапы: E1, E3, E5, E6, E7, E10.

**Security:**
1. **[Medium]** JWT: `Services/JwtService.cs` — алгоритм HS256, lifetime access-токена (дефолт 60 мин),
   **серверный токен с exp=9999** (фактически вечный — нет ротации). `Settings/JwtSettings.cs` —
   хранение ключа.
2. **[Low]** Пароли: `Services/PasswordHasher.cs` — BCrypt work factor 12 (ок), `FixedTimeEquals`
   при сравнении (ок), но принимаются **legacy несолёные SHA256-хэши** для совместимости — оценить
   план миграции/форс-смены.
3. **[High]** OTP/2FA: `Services/CodeGenerator.cs` — 6 цифр, CSPRNG (`RandomNumberGenerator.GetInt32`).
   Проверить **отсутствие лимита попыток** ввода кода в `Features/Auth`, `ConfirmAccount`,
   `ConfirmResetPassword` (онлайн-брутфорс 10⁶).
4. **[Medium]** Refresh-токены: `RefreshTokenGenerator.cs` (32 байта, CSPRNG, base64url — ок),
   `RefreshTokensStorage.cs` — **срок ~9999 дней**, ротация при логине того же устройства, уникальный
   индекс на value (миграция `SecurityHardening`). Оценить абсолютный срок и цепочку ротации.
5. **[High]** **Rate limiting/лок-аут отсутствует** (E6) на `Auth`, OTP, `ResetPassword` — есть только
   инкремент метрик. Спроектировать лимиты на пользователя/IP/устройство.
6. **[ок→подтвердить]** Anti-enumeration в `ResetPassword`: dummy `reset_id` для несуществующего юзера +
   рандомная задержка 100–300 мс. Проверить, что и тайминг, и ответ неотличимы.
7. **[Medium]** Серверные RPC `IdentityServerApiService` (`ForceSetPasswordServer`,
   `CreateSessionForUserServer`) под политикой `TokenType.Service` — подтвердить, что клиент не может
   получить сервисный токен.

**Performance:**
1. Storage-классы: `AsNoTracking` на чтениях, индексы по login и refresh-value.
2. Стоимость BCrypt(12) под пиком логинов — измерить throughput, оценить как вектор DoS.
3. `TokenRevocationCache` (in-memory `ConcurrentDictionary`) — рост памяти, потеря при рестарте,
   корректность фоновой очистки.

**Anchors:** `Services/JwtService.cs:15-66`, `Services/PasswordHasher.cs:6-38`,
`Services/CodeGenerator.cs:6-19`, `Services/RefreshTokenGenerator.cs`,
`Persistence/Services/RefreshTokensStorage.cs`, `Features/Auth/AuthCommandHandler.cs`,
`Features/ResetPassword/ResetPasswordCommandHandler.cs`, `Features/ConfirmResetPassword/*`,
`Host/IdentityServerApiService.cs:19`, `Persistence/Migrations/20260507005955_SecurityHardening.cs`.

### 3.3 Users — `Backend/BarkCloud.Users/`
Релевантные этапы: E1, E4, E5, E7, E9, E10.

**Security:**
1. **[High→проверить]** AuthZ профиля/устройств/контактов: клиентские vs серверные API; убедиться, что
   `RenameDevice`/`DeleteDevice`/`GetDevices` работают только со своими сущностями.
2. **[Medium]** Privacy: реально применяется только `SearchableByUsername` (в `SearchUsers`); прочие
   `*Visibility` — хранимые, но не enforced. Зафиксировать как недоработку контроля доступа к данным.
3. **[Medium]** Draft-flow (`AddDraftUser`/`OverrideDraftUser`/`ConfirmUser`) — гонки и повторное
   использование черновика; `DeleteAccount` — полнота каскада и событие `UserDeleted`.
4. **[Low]** Firebase push-токен в `UserDevice` — хранение, утечка в логах/ответах.

**Performance:**
1. **[Medium]** **`SearchUsers`** — `LIKE %term%` по Username/FirstName/LastName **без индекса/full-text
   и без курсорной пагинации** (`UsersStorage.cs:152-167`). Снять `EXPLAIN (ANALYZE)`, оценить на
   большой таблице; рассмотреть trigram/full-text индекс.
2. N+1 в маппинге профиля (`UserMapping`), индексы уникальности username/email.

**Anchors:** `Persistence/Services/UsersStorage.cs:152-167`, `Persistence/Services/DevicesStorage.cs`,
`Features/SearchUsers/*`, `Features/DeleteAccount/*`, `Infrastructure/UserInfoQueueSender.cs`,
`Domain/UserPrivacy.cs`.

### 3.4 Files — `Backend/BarkCloud.Files/`
Релевантные этапы: E1, E4, E7, E8, E9, E10.

**Security:**
1. **[Critical]** **IDOR в `GetTempDownloadUrl`**: обработчик берёт файлы по `FileId` **без проверки
   владения** — любой аутентифицированный пользователь может сгенерировать рабочую временную ссылку на
   чужой файл. Подтвердить и зафиксировать сценарий воспроизведения.
2. **[Critical]** **IDOR в `FilesServerApiService.GetFileData`**: выборка по `FileId` без проверки
   `userId ∈ Uploaders` (вопреки ожиданию). Любой держатель сервисного токена читает метаданные любого
   файла.
3. **[проверить]** AuthZ CloudApi: `ListDirectory`/`MoveFileEntry`/`RenameFileEntry`/`DeleteFileEntry`
   проверяют `OwnerId` (по разведке — ок). Искать пробелы и гонки в soft-delete/restore (корзина),
   проверять `AttachFile`, `ListUserMedia`, `Favorites`.
4. **[Medium]** Presigned URL: срок жизни и скоуп (`S3Uploader.cs`, `Features/GetUploadUrl`,
   `Features/GetTempDownloadUrl`), предсказуемость `FileId`.
5. **[High]** **Квота не проверяется ДО загрузки** (`Features/UploadFile`) — пользователь может
   превысить `StorageLimitBytes`. Спроектировать проверку перед выдачей upload-URL/записью.
6. **[High]** Медиа-обработка: `Services/VideoThumbnailExtractor.cs` — **нет таймаута ffmpeg**
   (зависание на «злом» видео = DoS) и угадываемый temp-путь в `/tmp`; `Services/ImageCompressor.cs` —
   есть лимиты 2500px/2МБ, но **нет таймаута `Image.LoadAsync`** (decompression-bomb / зависание).
7. **[Medium]** Дедуп: `FileHashesStorage.cs` (SHA256) — межпользовательский информационный лик
   (определение существования чужого файла по совпадению хэша).
8. **[Medium]** HTTP-контроллер `FilesController` (прямые upload/download): авторизация, path-traversal
   по идентификатору, content-type.
9. **[Low]** Консьюмер `UserDeleted`: осиротевшие S3-блобы не удаляются физически — рост хранилища.

**Performance:**
1. `CloudHierarchyStorage`/`AlbumStorage`/`FavoriteFilesStorage` — курсорная пагинация и `AsNoTracking`
   (по разведке — ок); подтвердить индексы под списки и обход поддерева директорий (итеративный, не
   рекурсивный CTE).
2. Фоновые воркеры `TempFileCleanupService`/`TrashCleanupService` — интервалы, батчинг, отсутствие
   блокировок.
3. Стоимость генерации превью под параллельной загрузкой; стриминг больших файлов (память, см. 512МБ).

**Anchors:** `Features/GetTempDownloadUrl/GetTempDownloadUrlCommandHandler.cs:28-88`,
`Host/FilesServerApiService.cs:26-34`, `Features/GetFileData/GetFileDataCommandHandler.cs`,
`Features/UploadFile/UploadFileCommandHandler.cs`, `Features/GetUploadUrl/*`,
`Services/VideoThumbnailExtractor.cs:45-48`, `Services/ImageCompressor.cs:12-17`,
`Persistence/CloudHierarchyStorage.cs`, `Persistence/FileHashesStorage.cs`, `Infrastructure/S3Uploader.cs`,
`Host/FilesController.cs`, `Consumers/UserDeletedConsumer.cs`.

### 3.5 Notification — `Backend/BarkCloud.Notification/`
Релевантные этапы: E2, E5, E9, E10.

**Security:**
1. **[Critical]** **SMTP TLS-валидация отключена**: `ServerCertificateValidationCallback => true` в
   `EmailSender.cs` — MITM/перехват писем (коды подтверждения!). Включить валидацию сертификата и
   проверку имени хоста.
2. **[ок→подтвердить]** HTML-экранирование: `HtmlEmailTemplateParser` применяет `WebUtility.HtmlEncode`
   к значениям payload — проверить покрытие всех плейсхолдеров (инъекция в письма через имя/bio).
3. **[Low]** PII в логах: `EmailMasker` (`***@domain`) — подтвердить, что адрес не утекает в полном виде.

**Performance / надёжность:**
1. **[Medium]** **Нет явной retry-политики/DLQ/идемпотентности** в `EmailQueueConsumer` и настройке
   MassTransit (`Program.cs`): отравленное сообщение → бесконечный retry; дубль доставки → дубль письма.
   Спроектировать ограниченный retry с backoff, dead-letter и идемпотентный ключ.

**Anchors:** `Senders/EmailSender.cs:37-45`, `Consumers/EmailQueueConsumer.cs:23-56`,
`Parsers/HtmlEmailTemplateParser.cs:26-46`, `Helpers/EmailMasker.cs`, `Program.cs`.

### 3.6 GrpcServer + Shared.* — `Backend/BarkCloud.GrpcServer/`, `Shared/*`
Релевантные этапы: E3, E5, E10 + кросс-сервисная производительность.

**Security:**
1. **[High]** Валидация входящего JWT: `XAuth/XAuthExtensions.cs` — issuer/audience/lifetime/signature,
   проверка `IdentityClaims.TokenType`, обращение к `TokenRevocationCache`. Подтвердить строгость и
   единообразие у всех сервисов.
2. **[Medium]** `TokenRevocationCache` — in-memory; рестарт обнуляет список отзыва (окно для отозванной
   сессии). Оценить переход на persistent/distributed-кэш.
3. **[Medium]** `RequestContextInterceptor` доверяет метаданным `X-Ip`/`X-Device`/`X-Os` от клиента —
   спуфинг (важно для геолокации/уведомлений о входе).
4. **[Medium]** `ServerExceptionInterceptor` — утечка внутренних деталей в gRPC-статусах/trailers.
5. **[Low]** `Shared.SecurityUtilities/SecurityUtilities.cs` — источник случайности (CSPRNG vs
   `Random`), наличие constant-time сравнения; `TlsSettings`.

**Performance:**
1. Перехватчики на горячем пути — аллокации, синхронные блокировки.
2. Метрики `MetricsCollector`/`MetricsReporterService` — стоимость, фоновые сервисы очистки.

**Anchors:** `XAuth/XAuthExtensions.cs:15-87`, `XAuth/TokenRevocationCache.cs`,
`XAuth/UserContext.cs`, `Tracker/RequestContextInterceptor.cs`, `ServerExceptionInterceptor.cs`,
`Metrics/*`, `Shared/BarkCloud.Shared.SecurityUtilities/SecurityUtilities.cs`, `Shared/BarkCloud.Shared.Auth/*`.

### 3.7 Web — `Backend/BarkCloud.Web/`
Релевантные этапы: E1, E2, E3, E4, E5, E6, E10 + web-специфика (CSRF/XSS/заголовки/RCE).

**Security:**
1. **[ок→подтвердить]** Cookies `bark_at/bark_rt/bark_did`: HttpOnly/Secure(`App:CookieSecure`)/
   SameSite=Lax — ок; локальная валидация JWT с issuer/audience/lifetime/signature и `ClockSkew=0`.
   Замечания: срок device-cookie 5 лет; отсутствие ротации токена при refresh.
2. **[High]** **CSRF отсутствует**: нет antiforgery, на upload явно `DisableAntiforgery()`; единственная
   защита — SameSite=Lax. Проверить все POST: `/login`, `/register`, `/register/confirm`, `/forgot`,
   `/forgot/confirm`, `/api/*`. Спроектировать токены CSRF и/или SameSite=Strict для изменяющих операций.
3. **[High]** **Security-заголовки отсутствуют** в `Program.cs`: нет CSP, HSTS, X-Frame-Options,
   X-Content-Type-Options. Добавить.
4. **[Medium]** XSS: `TemplateRenderer` (`{{ }}` JS-escape / `{{{ }}}` raw), `page_data_json` через
   безопасный JSON-encoder — ок; **avatar URL не валидируется** (`PageDataBuilder`) → риск
   `javascript:`/mixed-content. Проверить классификацию плейсхолдеров на каждой странице.
5. **[Critical/High]** **Admin + docker.sock**: `AdminGate` — пароль сверяется `FixedTimeEquals` (ок),
   cookie `bark_admin` подписан HMAC-SHA256 на общем JWT-секрете, срок 30 мин; **нет rate-limit на
   `/api/system/unlock`** (брутфорс пароля); пароль хранится в конфиге в открытом виде. `DockerService`
   использует `ProcessStartInfo.ArgumentList` — без shell-инъекции (подтвердить **все** вызовы).
   **Системный риск:** `docker.sock` + `user: root` в публично доступном web = полный контроль над
   хостом при обходе админ-гейта (RCE-поверхность). Оценить вынос в изолированный admin-сервис/сеть.
6. **[Medium]** Upload-прокси (`Endpoints/CloudApiEndpoints.cs` `files/upload`): авторизация есть,
   лимит 512МБ; **нет валидации content-type/расширения**; проверить, что назначение прокси не
   управляется клиентом (SSRF), и обработку `fileId` (path-traversal).
7. **[ок]** Open-redirect не обнаружен (роуты хардкод) — подтвердить отсутствие `returnUrl`.
8. **[Low]** Скрытые поля формы несут пароль/`code_id`/`reset_id` между шагами (2FA/регистрация/сброс) —
   MVP-упрощение; заменить на короткоживущий серверный pending-токен.

**Performance:**
1. gRPC-клиенты зарегистрированы через `AddGrpcClient` (синглтон-каналы, пулинг — ок);
   `HttpClient("files-upload")` — синглтон (ок); h2c внутрь.
2. `PageService` — кэширование чтения файлов страниц с диска; стриминг upload (память при 512МБ).
3. `PageDataBuilder.BuildShellAsync`/`BuildSettingsJsonAsync` — число gRPC-вызовов на сборку страницы,
   возможность параллелизации.

**Anchors:** `Auth/AuthGateway.cs:23-249`, `Auth/AdminGate.cs:39,66-83`,
`Infrastructure/DockerService.cs:319`, `Endpoints/SystemEndpoints.cs:24-31`,
`Endpoints/CloudApiEndpoints.cs:301-341`, `Infrastructure/TemplateRenderer.cs:44-65`,
`Rendering/PageDataBuilder.cs:88-90,182-216`, `Rendering/PageService.cs`, `Program.cs:54-76`.

### 3.8 Инфраструктура — `Backend/nginx/`, `docker-compose*.yml`, `sample.env`
Релевантные этапы: E1, E2 + конфигурация и hardening.

**Security:**
1. **[Medium]** nginx (`nginx/cloud.barkfluff.conf`): **self-signed cert + trust-all у клиентов**,
   h2c внутрь (plaintext в доверенной сети — оценить риск), `client_max_body_size` (внешний web-vhost
   должен быть 512m), security-заголовки на уровне прокси, версия/TLS-настройки.
2. **[High]** docker-compose: `web` с `user: root` и монтированием `docker.sock` (см. 3.7.5); сетевая
   сегментация `barkcloud-network`; наружу проброшен только порт web; секреты в `.env` (права доступа,
   отсутствие в git).
3. **[Medium]** PostgreSQL: единая БД с изоляцией по схемам, креды, доступ только из сети compose,
   бэкап-volume.
4. **[Medium]** MinIO: root-креды (`MINIO_ROOT_*`), политики бакетов (приватность, отсутствие public-read),
   `S3BucketInitializer`.
5. **[Medium]** RabbitMQ: креды/удаление гостя, доступ к management-порту.
6. **[Low]** Seq: `SEQ_ADMIN_PASSWORD`, ограничение доступа к логам.
7. **[High]** Образы: `dotnet list package --vulnerable` + `trivy`/`grype`; non-root `USER` в Dockerfile
   (кроме web, где root осознанно — задокументировать компромисс).

**Performance:**
1. Лимиты ресурсов контейнеров (CPU/RAM), пулы соединений к PostgreSQL, health-checks и порядок
   `depends_on`, буферизация/таймауты nginx для gRPC и больших аплоадов.

**Anchors:** `Backend/nginx/cloud.barkfluff.conf`, `Backend/docker-compose.yml`,
`Backend/docker-compose-dev.yml`, `Backend/sample.env`, `Backend/*/Dockerfile`,
`BarkCloud.Files/Infrastructure/S3BucketInitializer.cs`.

---

## 4. Нагрузочное тестирование и профилирование

1. **gRPC-нагрузка (`ghz`):** ключевые методы — `IdentityApi.Auth` (пик логинов, BCrypt),
   `CloudApi.ListUserMedia` (галерея, пагинация), `Files.GetTempDownloadUrl`, `UsersApi.SearchUsers`
   (потенциально медленный `LIKE`).
2. **HTTP-нагрузка (`k6`/`bombardier`):** `/login`, `/api/cloud/*`, загрузка файла через `/files/upload`.
3. **Профилирование (.NET):** `dotnet-counters monitor` (ThreadPool, GC, allocations), `dotnet-trace`
   для CPU-горячих путей, `dotnet-gc-dump` на утечки (рост `TokenRevocationCache`, буферы изображений).
4. **БД:** `EXPLAIN (ANALYZE, BUFFERS)` для `SearchUsers`, списков галереи/корзины/альбомов; проверка
   использования индексов и keyset-пагинации.
5. **Сценарии стресса:** одновременная массовая загрузка медиа (ffmpeg/ImageSharp — CPU/память/таймауты),
   глубокая иерархия папок, большая корзина/галерея, шторм писем (Notification retry).

---

## 5. Шаблон отчёта и чек-лист итогов

Каждая находка — строка таблицы:

| ID | Сервис | Этап (E#) | Описание | Файл:строка | Severity | Воспроизведение | Рекомендация | Статус |
|----|--------|-----------|----------|-------------|----------|------------------|--------------|--------|

Дополнительно:
- Сводка по severity (кол-во Critical/High/Medium/Low).
- Матрица покрытия: этапы E1–E10 × сервисы 3.1–3.8 (что проверено / не применимо / открыто).
- Список рекомендаций, отсортированный по severity и стоимости внедрения.

---

## Приложение A. Известные горячие точки (стартовые цели проверки)

Найдено при предварительной разведке кода — это **входные точки** для верификации, а не итоговый
вердикт. Каждую необходимо подтвердить и воспроизвести при выполнении аудита.

| Severity | Находка | Anchor |
|----------|---------|--------|
| Critical | `Configuration.GetConfiguration` отдаёт все секреты сервиса по сервисному токену | `Host/ConfigurationApiService.cs:30-55` |
| Critical | IDOR: `GetTempDownloadUrl` не проверяет владение файлом | `Features/GetTempDownloadUrl/GetTempDownloadUrlCommandHandler.cs:28-88` |
| Critical | IDOR: `FilesServerApi.GetFileData` читает метаданные по `FileId` без uploader-check | `Host/FilesServerApiService.cs:26-34` |
| Critical | SMTP-валидация TLS-сертификата отключена (`=> true`) | `Senders/EmailSender.cs:37-45` |
| High | Нет rate-limiting/лок-аута на `Auth`/OTP/`ResetPassword` | `Features/Auth/AuthCommandHandler.cs`, `Features/ResetPassword/*` |
| High | Нет rate-limiting на admin `/unlock` | `Endpoints/SystemEndpoints.cs:24-31`, `Auth/AdminGate.cs` |
| High | Квота хранилища не проверяется до загрузки | `Features/UploadFile/UploadFileCommandHandler.cs` |
| High | `docker.sock` + `user: root` в публичном web (RCE-поверхность на хост) | `Infrastructure/DockerService.cs`, `Backend/docker-compose.yml` |
| High | Нет CSRF-защиты (только SameSite=Lax) | `Endpoints/CloudApiEndpoints.cs:341`, `Endpoints/WebEndpoints.cs` |
| High | Отсутствуют security-заголовки (CSP/HSTS/X-Frame-Options/X-Content-Type-Options) | `Backend/BarkCloud.Web/Program.cs` |
| High | Нет таймаута ffmpeg при извлечении превью (DoS) | `Services/VideoThumbnailExtractor.cs:45-48` |
| Medium | refresh-токены ~9999 дней; серверные токены exp=9999 | `Persistence/Services/RefreshTokensStorage.cs`, `Services/JwtService.cs:41` |
| Medium | `TokenRevocationCache` только in-memory (теряется при рестарте) | `XAuth/TokenRevocationCache.cs` |
| Medium | `SearchUsers` — неиндексируемый `LIKE %..%` без курсора | `Persistence/Services/UsersStorage.cs:152-167` |
| Medium | Notification без retry/DLQ/идемпотентности | `Consumers/EmailQueueConsumer.cs:23-56`, `Program.cs` |
| Medium | Межпользовательский дедуп-лик по SHA256 | `Persistence/FileHashesStorage.cs` |
| Medium | Нет таймаута `Image.LoadAsync` (decompression-bomb) | `Services/ImageCompressor.cs:12-17` |
| Low | Legacy несолёные SHA256-хэши паролей принимаются | `Services/PasswordHasher.cs:21-27` |
| Low | Avatar URL не валидируется (`javascript:`/mixed-content) | `Rendering/PageDataBuilder.cs:88-90` |
| Low | Пароль/`code_id`/`reset_id` в скрытых полях формы | `Auth/RegistrationGateway.cs`, `Auth/PasswordResetGateway.cs` |

## Приложение B. Инструменты и команды

```bash
# Уязвимые NuGet-зависимости (по каждому проекту/солюшену)
dotnet list package --vulnerable --include-transitive

# Сканирование Docker-образов
trivy image barkcloud-files-dev:latest
grype barkcloud-web-dev:latest

# Секреты в git-истории
gitleaks detect --source . --redact

# gRPC-нагрузка (пример: Auth)
ghz --insecure --proto Shared/BarkCloud.Proto/identity_api.proto \
    --call barkcloud.identity.IdentityApi.Auth \
    -d '{"login":"u","password":"p"}' -c 50 -n 5000 localhost:7020

# HTTP-нагрузка веб-клиента
k6 run web-login.js        # или: bombardier -c 50 -n 5000 http://localhost:8080/login

# Профилирование .NET (внутри контейнера/хоста)
dotnet-counters monitor --process-id <pid>
dotnet-trace collect --process-id <pid>
dotnet-gc-dump collect --process-id <pid>

# План запроса PostgreSQL
EXPLAIN (ANALYZE, BUFFERS) SELECT ... ;  -- для SearchUsers и списков галереи/корзины
```

---

> Документ — методология. Подтверждение находок Приложения A, severity и формулировка ремедиации
> выполняются на этапе проведения аудита и фиксируются в таблице раздела 5.
