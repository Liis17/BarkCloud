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

- **Storage классы Backend** (`*Storage.cs` в `Persistence/Services/`) — будут переведены на интерфейсы `I*Storage` отдельным коммитом перед написанием Handler-тестов. До этого тестируются только классы без Storage-зависимостей.
- **gRPC клиенты** (`*ServerApiClient`) — наследуют `ClientBase<T>`, методы виртуальные, мокаются Moq напрямую.
- **MediatR** — `Mock<IMediator>`.
- **ILogger** — `NullLogger<T>.Instance` либо `Mock<ILogger<T>>`.
- **Client interceptors gRPC** — тестируются через harness `InterceptorTestHarness`, который перехватывает Metadata в continuation.
- **ServerCallContext** — реализация `TestServerCallContext` в `BarkCloud.TestKit`.

## Покрытие (текущее состояние)

Фаза A (мокаемые backend-пробелы — без рефактора) завершена; все `PlaceholderTests` заменены, кроме `Shared.Identity`/`Shared.Queue` (константы/DTO — тестировать нечего).

| Проект | Тестов | Покрытые компоненты |
|--------|-------:|---------------------|
| `BarkCloud.Identity.Tests` | 119 | 20/20 хендлеров (client + 6 `*Server` admin-вариантов), `Services/` (`JwtService`, `PasswordHasher`, `CodeGenerator`, `RefreshTokenGenerator`), консьюмеры |
| `BarkCloud.Users.Tests` | 69 | Все хендлеры (Devices×7, Privacy×2, Search/ListByIds/Contacts, ProfilePicture×2, ProfileServer, StorageLimit и пр.) + `SessionRevokedConsumer` |
| `BarkCloud.Web.Tests` | 49 | Rendering (`Format`, `FileKind`, `CloudJson`), `AuthGateway` (маппинг x-error-code → `LoginOutcome`) |
| `BarkCloud.Files.Tests` | 34 | Хендлеры за `I*Storage` (`UploadFile` и др.), `ImageCompressor`, `SessionRevokedConsumer`. Album/Cloud-хендлеры + `GetFileData`/`GetFilesData` ждут рефактора (фаза B) |
| `BarkCloud.Shared.SecurityUtilities.Tests` | 23 | `SecurityUtilities.EvaluatePasswordStrength`, `GetPasswordStrengthMessage` |
| `BarkCloud.GrpcServer.Tests` | 17 | `TokenRevocationCache`, `MetricsCollector`, `ServerExceptionInterceptor` |
| `BarkCloud.Notification.Tests` | 9 | `EmailMasker`, `HtmlEmailTemplateParser`, `EmailQueueConsumer` |
| `BarkCloud.Shared.Auth.Tests` | 8 | Все 6 client-interceptor'ов (`JwtClientInterceptor`, `XAppClientInterceptor`, `XOsClientInterceptor`, `XDeviceClientInterceptor`, `XDeviceIdInterceptor`, `XIpClientInterceptor`) |
| `BarkCloud.Shared.Exceptions.Tests` | 4 | `ExceptionClientInterceptor` (маппинг error code → доменное исключение) |
| `BarkCloud.Configuration.Tests` | placeholder | ждёт рефактора `ConfigurationStorage`→`IConfigurationStorage` (фаза B) |
| `BarkCloud.Shared.Identity.Tests` / `BarkCloud.Shared.Queue.Tests` | placeholder | константы/DTO-records — тестировать нечего |

Дальше: **фаза B** — рефактор `I*Storage` (Album/Share/Favorite/CloudHierarchy + Configuration) и тесты Files/Configuration-хендлеров; **фаза C** — клиенты (iOS pure-logic + iOS CI-джоба, Android ViewModels/репозитории).

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

Workflow `.github/workflows/tests.yml`:
- **`dotnet-tests`** — `runs-on: self-hosted`, прогоняет `dotnet test` на всём solution с coverage и публикацией TRX-отчёта.
- **`android-tests`** — `runs-on: ubuntu-latest`, прогоняет `./gradlew :app:testDebugUnitTest`.
- **`ios-tests`** — будет добавлен в этапе P3 (требует macOS-раннера).

Триггеры: push в `dev`/`master`/`claude/**`, pull_request в `dev`/`master`, workflow_dispatch.
