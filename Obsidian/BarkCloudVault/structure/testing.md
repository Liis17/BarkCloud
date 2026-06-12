# Юнит-тесты

Parent: [[index]]

## Назначение

Юнит-тесты для всех модулей проекта BarkCloud. Цель — покрыть Features/Services/Consumers/Interceptors/Repositories/ViewModels во всех платформах (Backend .NET, Shared .NET, Android Kotlin, iOS Swift). Подход — **моки везде**, без EF Core InMemory и без Testcontainers.

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
| `BarkCloud.Identity.Tests` | 119 | 20/20 хендлеров (client + 6 `*Server` admin-вариантов), `Services/` (`JwtService`, `PasswordHasher`, `CodeGenerator`, `RefreshTokenGenerator`), консьюмеры |
| `BarkCloud.Users.Tests` | 70 | Все хендлеры (Devices×7, Privacy×2, Search/ListByIds/Contacts, ProfilePicture×2, ProfileServer, StorageLimit и пр.) + `SessionRevokedConsumer` |
| `BarkCloud.Web.Tests` | 49 | Rendering (`Format`, `FileKind`, `CloudJson`), `AuthGateway` (маппинг x-error-code → `LoginOutcome`) |
| `BarkCloud.Files.Tests` | 167 | 43/44 хендлеров (Album×7, Cloud×26 — директории/корзина/шеринг/избранное/медиа, `GetFileData`/`GetFilesData`, `UploadFile` и др.), сервисы `ImageCompressor`/`AlbumViewBuilder`/`PhysicalStorageStatsProvider`, `SessionRevokedConsumer`. Пропущены: `UploadAvatarServer` (линейный S3/image-IO, `ImageCompressor` не `virtual`), `UserDeletedConsumer` (прямые `ExecuteDeleteAsync` по `FilesContext`), `VideoThumbnailExtractor`/`PreviewPersistenceService`/`*CleanupService` (IO/таймеры) |
| `BarkCloud.Shared.SecurityUtilities.Tests` | 23 | `SecurityUtilities.EvaluatePasswordStrength`, `GetPasswordStrengthMessage` |
| `BarkCloud.GrpcServer.Tests` | 17 | `TokenRevocationCache`, `MetricsCollector`, `ServerExceptionInterceptor` |
| `BarkCloud.Notification.Tests` | 9 | `EmailMasker`, `HtmlEmailTemplateParser`, `EmailQueueConsumer` |
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

## CI

Workflow `.github/workflows/tests.yml` — гранулярный запуск по изменённым путям через `dorny/paths-filter@v4` для pull request и ручных прогонов:
- **`changes`** — джоба-диспетчер: определяет изменённые части (per-микросервис, `shared`, `android`) и выдаёт outputs. Изменения в `Shared/**` или `Tests/BarkCloud.TestKit/**` триггерят все backend-тесты (микросервисы зависят от Shared/Proto).
- **`test-<сервис>`** (configuration/files/grpcserver/identity/notification/users/web) — `runs-on: [self-hosted, linux]`, каждая гоняет только свой `.Tests`-проект; `if`: изменена своя папка **или** `shared`.
- **`test-shared`** — все `Shared.*.Tests` одним прогоном при изменении `Shared/**`.
- **`android-tests`** — `runs-on: ubuntu-latest`, `./gradlew :app:testDebugUnitTest`, только при изменениях в `Android/**`.
- **`ios-tests`** — будет добавлен в этапе P3 (требует macOS-раннера).

Backend deploy-воркфлоу `build-backend-*.yml` вызывают общий reusable workflow `.github/workflows/backend-service-ci.yml`:
- **`changes`** — проверяет runtime-изменения (`Backend/BarkCloud.<Service>/**`, `Shared/**`, `Backend/rebuild.trigger`) и test-only изменения (`Tests/Backend/BarkCloud.<Service>.Tests/**`, `Tests/BarkCloud.TestKit/**`).
- **`check-dotnet`** — проверяет .NET 10.0 SDK на `[self-hosted, linux]`.
- **`test`** — сначала запускает тесты конкретного сервиса. При падении отправляет Telegram-сообщение с inline-кнопкой на текущий GitHub Actions run, а сборка не стартует.
- **`build`** — запускается только после успешных тестов и только при runtime-изменениях или ручном запуске. Публикует Docker-образ и отправляет Telegram-сообщение об успехе или провале с кнопкой на GitHub Actions run.

Docker-теги сохраняют прежнее правило: для ветки `dev` используется постфикс `-dev`, для `master` — имя образа без постфикса. Например, `barkcloud-files-dev:<sha>` в `dev` и `barkcloud-files:<sha>` в `master`.

Drive (`Drive/*`, WPF/Windows, тестов нет) в CI не собирается — только локально. Backend-воркфлоу `build-backend-*.yml` привязаны к `[self-hosted, linux]`, чтобы не уехать на Windows self-hosted runner (общий label `self-hosted`).

Триггеры:
- `tests.yml`: pull_request в `dev`/`master`, workflow_dispatch.
- `build-backend-*.yml`: push в `dev`/`master` по путям конкретного сервиса, `Shared/**`, его тестам, `Tests/BarkCloud.TestKit/**`, `Backend/rebuild.trigger`; также workflow_dispatch.
