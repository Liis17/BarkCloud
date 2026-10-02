# Тестирование

Parent: [[index]]

## Назначение

Юнит-тесты для всех модулей проекта BarkCloud. Цель — покрыть Features/Services/Consumers/Interceptors/Repositories/ViewModels во всех платформах (Backend .NET, Shared .NET, Android Kotlin, iOS Swift). Основной подход — моки на границах хендлеров; генерация ID и миграции Users дополнительно проверяются на реальном PostgreSQL. Без EF Core InMemory и без Testcontainers.

## Расположение

Тестовые проекты лежат в корневой папке `Tests/`, зеркалируя структуру `Backend/` и `Shared/`:

```
Tests/
├── Directory.Build.props             — общие версии xUnit/Moq/FluentAssertions, опции компилятора
├── BarkCloud.TestKit/                — общий вспомогательный проект (NullLogger, TestServerCallContext)
├── Backend/
│   ├── BarkCloud.Configuration.Tests/
│   ├── BarkCloud.Files.Tests/
│   ├── BarkCloud.GrpcServer.Tests/
│   ├── BarkCloud.Identity.Tests/
│   ├── BarkCloud.Notification.Tests/
│   ├── BarkCloud.Users.Tests/
│   └── BarkCloud.Web.Tests/
└── Shared/
    ├── BarkCloud.Shared.Auth.Tests/
    ├── BarkCloud.Shared.Exceptions.Tests/
    ├── BarkCloud.Shared.Identity.Tests/
    ├── BarkCloud.Shared.Queue.Tests/
    └── BarkCloud.Shared.SecurityUtilities.Tests/
```

Подключены в `BarkCloud.slnx` папками `/Tests/`, `/Tests/Backend/`, `/Tests/Shared/`.

## Стек

| Что | Пакет | Версия |
|-----|-------|--------|
| Test runner | `xunit` | 2.9.2 |
| Mocking | `Moq` | 4.20.72 |
| Assertions | `FluentAssertions` | 6.12.2 |
| Test SDK | `Microsoft.NET.Test.Sdk` | 17.11.1 |
| Coverage | `coverlet.collector` | 6.0.2 |

Версии управляются централизованно через `Tests/Directory.Build.props`. В каждом тестовом проекте — только `ProjectReference` на тестируемый проект и (при необходимости) дополнительные `PackageReference` для gRPC/Logging.

SQLite-тесты (`Configuration.Tests`, `Files.Tests`, `Identity.Tests`) закреплены на `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`: `Microsoft.EntityFrameworkCore.Sqlite 10.0.x` тянет `2.1.11` с нативным SQLite 3.49.1 (CVE-2025-6965, `NU1903`), а в `2.1.12` — SQLite 3.53.3. Пин стоит прямой ссылкой в каждом из трёх проектов (не в `Tests/Directory.Build.props` — SQLite нужен не всем); убрать, когда EF Core сам перейдёт на `>= 2.1.12`. См. [[modules/backend-audit]] (Q02).

## Стратегия мокирования

- **Storage классы Backend** (`*Storage.cs` в `Persistence/Services/`) — переведены на интерфейсы `I*Storage` (Files: `IAlbumStorage`/`IShareStorage`/`IFavoriteFilesStorage`/`ICloudHierarchyStorage`/`IUploadedFilesStorage`/`IFileHashesStorage`; `IConfigurationStorage`), хендлеры инжектят интерфейс и мокаются Moq.
- **gRPC клиенты** (`*ServerApiClient`) — наследуют `ClientBase<T>`, методы виртуальные, мокаются Moq напрямую.
- **MediatR** — `Mock<IMediator>`.
- **ILogger** — `NullLogger<T>.Instance` либо `Mock<ILogger<T>>`.
- **Client interceptors gRPC** — тестируются через harness `InterceptorTestHarness`, который перехватывает Metadata в continuation.
- **ServerCallContext** — реализация `TestServerCallContext` в `BarkCloud.TestKit`.

## Покрытие (текущее состояние)

Фазы A и B завершены. Фаза A — мокаемые backend-пробелы без рефактора; фаза B — рефактор `I*Storage` (Files: Album/Share/Favorite/CloudHierarchy; Configuration) и тесты всех EF-зависимых хендлеров. Все `PlaceholderTests` заменены, кроме `Shared.Identity`/`Shared.Queue` (константы/DTO — тестировать нечего).

| Проект | Тестов | Покрытые компоненты |
|--------|-------:|---------------------|
| `BarkCloud.Identity.Tests` | 309 | 20/20 хендлеров (client + 6 `*Server` admin-вариантов), `Services/` (`JwtService`, `PasswordHasher`, `CodeGenerator`, `RefreshTokenGenerator`, `AuthRateLimiter`), консьюмеры; хранилища на SQLite (`AuthPropertiesStorage*`, `AttemptCountersStorage`, счётчики попыток `reset_id`/кода, параллельные запросы → ровно `max`); лимиты F14 в `Auth`/`ResetPassword`/`ConfirmResetPassword`/`CreateAccount`/`ConfirmAccount`/TOTP/`SetPassword`/WebAuthn; F19: `LocationClient` (таймаут), `SessionIssuer`, `NotificationOutbox`/`NotificationOutboxWorker` на SQLite, `PasswordChangedNotifier` |
| `BarkCloud.Users.Tests` | 81 юнит + 8 PostgreSQL | Все хендлеры (Devices×7, Privacy×2, Search/ListByIds/Contacts, ProfilePicture×2, ProfileServer, StorageLimit и пр.) + `SessionRevokedConsumer`; ID пользователей, миграции sequence и конкурентное создание |
| `BarkCloud.Users.IntegrationTests` | 20 PostgreSQL | Уникальность логинов, конфликты записи, гонки переименования/вставки/подтверждения черновика и появления черновика между проверками, миграция F04 и откат |
| `BarkCloud.Web.Tests` | 134 | Rendering (`Format`, `FileKind`, `CloudJson`), `AuthGateway` (маппинг x-error-code → `LoginOutcome`, в т.ч. `TooManyAttempts`), `BrowserContext.ResolveIp`/`DeviceInfo.ToMetadata` (`x-real-ip`), `AdminUnlockLimiter`, `AdminGate` и др. |
| `BarkCloud.Files.Tests` | 167 | 43/44 хендлеров (Album×7, Cloud×26 — директории/корзина/шеринг/избранное/медиа, `GetFileData`/`GetFilesData`, `UploadFile` и др.), сервисы `ImageCompressor`/`AlbumViewBuilder`/`PhysicalStorageStatsProvider`, `SessionRevokedConsumer`. Пропущены: `UploadAvatarServer` (линейный S3/image-IO, `ImageCompressor` не `virtual`), `UserDeletedConsumer` (прямые `ExecuteDeleteAsync` по `FilesContext`), `VideoThumbnailExtractor`/`PreviewPersistenceService`/`*CleanupService` (IO/таймеры) |
| `BarkCloud.Shared.SecurityUtilities.Tests` | 23 | `SecurityUtilities.EvaluatePasswordStrength`, `GetPasswordStrengthMessage` |
| `BarkCloud.GrpcServer.Tests` | 41 | `TokenRevocationCache`, `MetricsCollector`, `ServerExceptionInterceptor`, `SourceIpResolver` (подделанные `x-ip-address`/XFF игнорируются, IPv6 → /64) |
| `BarkCloud.Notification.Tests` | 34 | `EmailMasker`, `HtmlEmailTemplateParser`, `EmailQueueConsumer`, `EmailConfiguration`, `EmailSender` (локальный SMTP/TLS-стенд) |
| `BarkCloud.Shared.Auth.Tests` | 8 | Все 6 client-interceptor'ов (`JwtClientInterceptor`, `XAppClientInterceptor`, `XOsClientInterceptor`, `XDeviceClientInterceptor`, `XDeviceIdInterceptor`, `XIpClientInterceptor`) |
| `BarkCloud.Shared.Exceptions.Tests` | 4 | `ExceptionClientInterceptor` (маппинг error code → доменное исключение) |
| `BarkCloud.Configuration.Tests` | 13 | Все 6 хендлеров за `IConfigurationStorage` (AddReservedName, DeleteReservedName, GetConfiguration, GetReservedNames, UpdateConfiguration, UpdateReservedName) |
| `BarkCloud.Shared.Identity.Tests` / `BarkCloud.Shared.Queue.Tests` | placeholder | константы/DTO-records — тестировать нечего |

Дальше: **фаза C** — клиенты (iOS pure-logic `AssetHashStore`/`CloudPresenceTracker`/`BarkRefreshable` + iOS CI-джоба, Android ViewModels/репозитории через mockk+turbine).

## Команды запуска

```bash
# Локально
dotnet restore BarkCloud.slnx
dotnet build BarkCloud.slnx -c Release
dotnet test BarkCloud.slnx -c Release --collect:"XPlat Code Coverage"

# Отдельный проект
dotnet test Tests/Backend/BarkCloud.Identity.Tests/BarkCloud.Identity.Tests.csproj
```

## PostgreSQL-тесты Users (F05)

`Tests/Backend/BarkCloud.Users.Tests/Persistence/UsersStoragePostgresTests.cs` использует реальные `UsersContext`, миграции и `UsersStorage`; категория — `PostgreSQL`. В `_Helpers/` находятся `PostgresUsersDatabase`, `PostgresFactAttribute` и `PostgresTheoryAttribute`.

- `BARKCLOUD_TEST_POSTGRES` — строка подключения к отдельному тестовому серверу PostgreSQL. Роли нужны права создания БД и применения миграций (в CI используется `postgres`).
- Каждый сценарий создаёт БД `barkcloud_users_f05_<guid>`, применяет нужные миграции и удаляет только эту БД после завершения. Пул соединений ограничен 20.
- Без строки подключения локальные тесты явно пропускаются. При `CI=true` отсутствие строки или недоступность PostgreSQL приводит к ошибке.
- Проверяются первый ID `1`, сохранение старого ID `4_000_000_000` и его связей, оба состояния `is_called` у опережающей sequence, пустая таблица после удаления пользователей, откат транзакции миграции, 200 конкурентных созданий с отдельными контекстами (до 20 одновременно) и повторный запуск миграций.

```bash
export BARKCLOUD_TEST_POSTGRES='Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres'
# Все тесты Users, включая PostgreSQL
dotnet test Tests/Backend/BarkCloud.Users.Tests/BarkCloud.Users.Tests.csproj -c Release
# Только PostgreSQL
dotnet test Tests/Backend/BarkCloud.Users.Tests/BarkCloud.Users.Tests.csproj -c Release --filter 'Category=PostgreSQL'
```

Сервис `postgres:18` с healthcheck `pg_isready` и переменная подключения включены для Users в `tests.yml` (`test-users`), `tests-backend-manual.yml` (элемент matrix `users`) и `backend-service-ci.yml` (тестовый job сборки Users). Для остальных сервисов PostgreSQL не запускается.

## Интеграционные тесты уникальности логинов (F04)

Отдельный проект `Tests/Backend/BarkCloud.Users.IntegrationTests/BarkCloud.Users.IntegrationTests.csproj` проверяет реальные `UsersStorage`, обработчик повторной регистрации и миграции. Он запускается отдельно от `BarkCloud.slnx`, поэтому обычный запуск юнит-тестов не требует БД из-за F04.

- Нужен `BARKCLOUD_TEST_POSTGRES` с доступом к отдельному тестовому PostgreSQL и правом `CREATE DATABASE`. Без строки подключения тесты завершаются ошибкой, а не пропускаются.
- Каждый сценарий создаёт и удаляет только свою БД `barkcloud_users_f04_<guid>`. Docker/Testcontainers не требуются: локально можно использовать отдельный сервер PostgreSQL.
- Проверяются регистронезависимые ограничения БД, доменные ошибки, повторная запись после конфликта, собственное имя, атомарный откат обновления профиля/контакта, конкурентное подтверждение черновика, отказ миграции на дублях с диагностикой ID, `Up → Down → Up` и непустой фильтр email.
- Гонки переименования и вставки синхронизируются перехватчиком SQL с барьерами до записи или после проверки занятости. Отдельно проверяется появление черновика между поиском email и username: повтор по тому же email получает `UserIsDraftException`. В конкурентных вставках заданы разные ID, чтобы проверка F04 не зависела от генератора F05.
- CI: отдельный `test-users-integration` в `.github/workflows/tests.yml`, PostgreSQL 18, RabbitMQ 4.1, .NET 10 и публикация TRX. Изменения интеграционного проекта включены в фильтр Users.

```bash
export BARKCLOUD_TEST_POSTGRES='Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres'
export BARKCLOUD_TEST_RABBITMQ='rabbitmq://integration:integration@127.0.0.1:5672/'
dotnet test Tests/Backend/BarkCloud.Users.IntegrationTests/BarkCloud.Users.IntegrationTests.csproj -c Release
```

## Транзакции и outbox F11

Users integration дополнительно требует `BARKCLOUD_TEST_RABBITMQ` — URI отдельного тестового RabbitMQ с доступом к vhost. Наблюдатели создают временные очереди; сетевой сбой моделируется локальным TCP proxy одного отправителя, общий брокер не останавливается. Проверяются сохранение/откат `UserDeleted`, отмена commit, доставка после восстановления сети и пересоздания host/DI, все профильные события в одном scope и up/down/up миграции outbox.

`Tests/Backend/BarkCloud.Identity.IntegrationTests` использует `BARKCLOUD_TEST_POSTGRES`; создаёт и удаляет только свои БД `barkcloud_identity_f11_<guid>`. Проверяет атомарную очистку аккаунта, повторную обработку, rollback и конкурентное подтверждение сброса пароля, оба значения `RevokeOtherSessions`, commit до уведомления, а также rollback/успех logout и двух обработчиков удаления сессии. Для F19 дополнительно: два воркера outbox уведомлений делят 60 строк (каждая доставляется ровно раз), захват строки с истёкшим lease, сценарий «Users недоступен после записи пароля — `SetPassword` успешен, письмо доставлено после восстановления», миграция `AddPendingNotifications` `Down → Up` (всего 22 теста). Отдельный `test-identity-integration` в CI запускается при изменениях Identity или Shared. Оба интеграционных проекта включены в `BarkCloud.slnx` и не пропускают проверки при отсутствии необходимого подключения. См. [[modules/transactional-outbox]], [[modules/notification-outbox]].

```bash
dotnet test Tests/Backend/BarkCloud.Identity.IntegrationTests/BarkCloud.Identity.IntegrationTests.csproj -c Release
```

## CI

Workflow `.github/workflows/tests.yml` — гранулярный запуск по изменённым путям через `dorny/paths-filter@v4` для pull request и ручных прогонов:
- **`changes`** — джоба-диспетчер на `ubuntu-latest`: определяет изменённые части (per-микросервис, `shared`, `android`) и выдаёт outputs. Изменения в `Shared/**` или `Tests/BarkCloud.TestKit/**` триггерят все backend-тесты (микросервисы зависят от Shared/Proto).
- **`test-<сервис>`** (configuration/files/grpcserver/identity/notification/users/web) — `runs-on: ubuntu-latest`, каждая гоняет только свой `.Tests`-проект; `if`: изменена своя папка **или** `shared`.
- **`test-shared`** — все `Shared.*.Tests` одним прогоном на `ubuntu-latest` при изменении `Shared/**`.
- **`android-tests`** — `runs-on: ubuntu-latest`, `./gradlew :app:testDebugUnitTest`, только при изменениях в `Android/**`.
- **`ios-tests`** — будет добавлен в этапе P3 (требует macOS-раннера).

Backend deploy-воркфлоу `build-backend-*.yml` вызывают общий reusable workflow `.github/workflows/backend-service-ci.yml`:
- **`changes`** — проверяет runtime-изменения (`Backend/BarkCloud.<Service>/**`, `Shared/**`, `Backend/rebuild.trigger`) и test-only изменения (`Tests/Backend/BarkCloud.<Service>.Tests/**`, `Tests/BarkCloud.TestKit/**`).
- **`check-dotnet`** — проверяет .NET 10.0 SDK на `ubuntu-latest`.
- **`test`** — сначала запускает тесты конкретного сервиса. При падении отправляет Telegram-сообщение с inline-кнопкой на текущий GitHub Actions run, а сборка не стартует.
- **`build`** — запускается только после успешных тестов и только при runtime-изменениях или ручном запуске. Публикует Docker-образ и отправляет Telegram-сообщение об успехе или провале с кнопкой на GitHub Actions run.

`tests-backend-manual.yml` запускает backend matrix-тесты и `Shared.*.Tests` вручную на `ubuntu-latest`.

Docker-теги считает локальная экшн `.github/actions/docker-version`: следующий patch-SemVer по тегам реестра (первая сборка — `1.0.0`) для репозитория с суффиксом ветки (`dev` → `-dev`, `nightly` → `-nightly`, `master` — без суффикса). Пушатся три тега: `<version>`, `latest` и `<sha>` (коммит). Telegram-уведомление об успехе показывает SemVer-тег. Например, `barkcloud-files-dev:1.2.3` в `dev` и `barkcloud-files:1.2.3` в `master`.

Drive (`Drive/*`, WPF/Windows, тестов нет) в CI не собирается — только локально. Backend-воркфлоу `build-backend-*.yml` выполняются на GitHub-hosted runner `ubuntu-latest`.

Триггеры:
- `tests.yml`: pull_request в `dev`/`master`, workflow_dispatch.
- `build-backend-*.yml`: push в `dev`/`master` по путям конкретного сервиса, `Shared/**`, его тестам, `Tests/BarkCloud.TestKit/**`, `Backend/rebuild.trigger`; также workflow_dispatch.

Гранулярность proto (deploy-воркфлоу): чтобы правка одного `.proto` не пересобирала весь бэкенд, в `paths` каждого `build-backend-*.yml` весь `Shared/BarkCloud.Proto/**` исключён из общего `Shared/**` (`!Shared/BarkCloud.Proto/**`) и точечно возвращён только нужный контракт — по принципу «сервис-владелец» (тот, у кого `GrpcServices="Server"`):
- `configuration_api.proto` → Configuration; `files_api.proto` → Files; `identity_api.proto` → Identity; `users_api.proto` → Users.
- `shared.proto` владельца не имеет (общие типы, `GrpcServices="None"`) → триггерит всех потребителей: Files, Identity, Users, Web.
- Notification proto не использует — для него возвращать нечего.
- Клиентские зависимости (`GrpcServices="Client"`) сборку НЕ триггерят: например правка `files_api.proto` не пересоберёт Users/Web, хотя они его клиенты. Компромисс: их сгенерированные стабы останутся со старым контрактом до их же следующей пересборки. `tests.yml` это не затрагивает — там `Shared/**` по-прежнему гоняет все backend-тесты.

## F10: отзыв сессий

`TokenRevocationCacheTests`, `RevocationSyncServiceTests` и `GrpcRevocationFeedTests` проверяют исходное время, max-upsert, снимок/дельту, ретраи, отмену, сохранение кэша при сбое, рестарт и две реплики. Тест с настоящим Kestrel проверяет блокировку порта до снимка. Identity SQLite: `RefreshTokensStorageRevocationTests` проверяет atomic rollback, safe logout, bulk/exclusion, TTL/cleanup и идемпотентность; `SessionRevocationApiServiceTests` — фильтры feed. Configuration SQLite проверяет появление ключей Identity в существующих развёртываниях. Подробности результатов и ещё не выполненных Docker-сценариев — [[modules/session-revocation]].
