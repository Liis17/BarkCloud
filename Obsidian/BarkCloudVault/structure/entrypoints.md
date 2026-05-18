# Точки входа

Parent: [[index]] · See also: [[structure/overview]]

## Backend-сервисы

Каждый микросервис — самостоятельное .NET-приложение со своим `Program.cs`.

| Сервис | Точка входа | Образ Docker |
|--------|-------------|--------------|
| Configuration | `Backend/BarkCloud.Configuration/Program.cs` | `barkcloud-configuration-dev:latest` |
| Identity | `Backend/BarkCloud.Identity/Program.cs` | `barkcloud-identity-dev:latest` |
| Users | `Backend/BarkCloud.Users/Program.cs` | `barkcloud-users-dev:latest` |
| Files | `Backend/BarkCloud.Files/Program.cs` | `barkcloud-files-dev:latest` |

`BarkCloud.GrpcServer` — **не запускаемый** проект, а общая библиотека-хост, используемая всеми четырьмя сервисами для подъёма gRPC, метрик, перехватчиков и Serilog. Подключается через `WebApplicationBuilderExtensions`, `ServiceCollectionExtensions`. См. [[modules/backend-grpcserver]].

## Порядок зависимостей (из docker-compose-dev.yml)

```
configuration   ←──┐
identity     ─────┤
users        ─────┤ (все ожидают configuration)
files        ─────┘
```

Все микросервисы при старте читают `CONFIGURATION_SERVICE_URL`, чтобы запросить свои настройки из `Configuration`-сервиса.

## Android-клиент

- Корень проекта: `Android/BarkCloud.Android/`
- Application ID: `com.barkfluff.BarkCloud`
- Точка входа Kotlin: `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/`
- Сборка: `./gradlew assembleDebug` или через Android Studio
