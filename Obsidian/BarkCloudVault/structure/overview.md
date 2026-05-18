# Структура проекта

Parent: [[index]]

## Дерево директорий

```
BarkCloud/
├── BarkCloud.slnx              — Solution (.slnx, новый XML-формат)
├── Android/
│   └── BarkCloud.Android/      — нативный Android-клиент (Kotlin DSL gradle)
│       ├── app/
│       ├── build.gradle.kts
│       ├── settings.gradle.kts
│       └── gradle/
├── Backend/
│   ├── docker-compose-dev.yml  — dev-окружение (микросервисы + инфраструктура)
│   ├── BarkCloud.Configuration/— сервис настроек
│   ├── BarkCloud.Identity/     — авторизация, токены, 2FA
│   ├── BarkCloud.Users/        — профили, устройства, контакты
│   ├── BarkCloud.Files/        — файлы, стикеры, бейджи
│   └── BarkCloud.GrpcServer/   — общий хост для gRPC-серверов
└── Shared/
    ├── BarkCloud.Proto/                — .proto-контракты
    ├── BarkCloud.Shared.Auth/          — gRPC interceptors (заголовки/JWT)
    ├── BarkCloud.Shared.Exceptions/    — кастомные gRPC-исключения
    ├── BarkCloud.Shared.Identity/      — константы Identity
    ├── BarkCloud.Shared.Queue/         — контракты RabbitMQ
    └── BarkCloud.Shared.SecurityUtilities/ — утилиты безопасности
```

## Описание директорий

### `/Backend/`
Содержит все .NET-микросервисы и единый docker-compose для dev-окружения. Каждый микросервис — отдельный проект с одинаковой структурой:

- `Domain/` — доменные сущности (POCO)
- `Features/` — фичи в стиле vertical-slice (каждая фича = папка с handler-ом)
- `Host/` — реализации gRPC-сервисов (`XxxApiService`, `XxxServerApiService`)
- `Persistence/` — EF Core DbContext, storage-классы, миграции
- `Services/` — вспомогательные сервисы (генераторы, хешеры, обработчики)
- `Consumers/` — обработчики сообщений RabbitMQ
- `Infrastructure/` — DI и инфраструктурные расширения
- `Migrations/` — EF Core миграции
- `Program.cs` — точка входа
- `Dockerfile` / `Dockerfile.slim` — образы для развёртывания

### `/Shared/`
Общие .NET библиотеки, используемые несколькими микросервисами и/или клиентами:

- **BarkCloud.Proto** — единственный источник правды по контрактам gRPC (см. [[modules/shared-proto]])
- **BarkCloud.Shared.Auth** — клиентские/серверные interceptors для метаданных gRPC (JWT, X-Device, X-App, X-Ip, X-Os)
- **BarkCloud.Shared.Exceptions** — таксономия gRPC-исключений по доменам (FastAuth, Files, Identity, Navigator, Users)
- **BarkCloud.Shared.Identity** — `IdentityClaims`, `ServiceId`, `TokenType`
- **BarkCloud.Shared.Queue** — DTO-контракты RabbitMQ для межсервисных событий
- **BarkCloud.Shared.SecurityUtilities** — общие криптоутилиты

### `/Android/`
Нативный Android-клиент. Namespace `com.barkfluff.BarkCloud`, minSdk 35, targetSdk 36, Kotlin DSL gradle. См. [[modules/android-app]].

## Конфигурационные файлы

| Файл | Назначение |
|------|-----------|
| `BarkCloud.slnx` | Solution .NET в новом XML-формате |
| `Backend/docker-compose-dev.yml` | Поднимает 4 сервиса + Postgres + RabbitMQ + MinIO + Seq |
| `Backend/*/appsettings.json` | Базовые настройки сервиса |
| `Backend/*/appsettings.Development.json` | Override для dev |
| `Backend/*/Dockerfile`, `Dockerfile.slim` | Образы |
| `Android/BarkCloud.Android/build.gradle.kts` | Android-сборка |
| `Android/BarkCloud.Android/settings.gradle.kts` | Gradle-настройки, repositories |
| `Android/BarkCloud.Android/local.properties` | Локальные настройки SDK (не коммитится обычно) |

## Связанные заметки

- [[structure/entrypoints]] — где `Program.cs` каждого сервиса и как они запускаются
- [[structure/infrastructure]] — что поднимает docker-compose
