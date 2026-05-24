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
| Web | `Backend/BarkCloud.Web/Program.cs` | `barkcloud-web-dev:latest` |

`BarkCloud.GrpcServer` — **не запускаемый** проект, а общая библиотека-хост, используемая всеми четырьмя gRPC-сервисами для подъёма gRPC, метрик, перехватчиков и Serilog. Подключается через `WebApplicationBuilderExtensions`, `ServiceCollectionExtensions`. См. [[modules/backend-grpcserver]].

`BarkCloud.Web` — ASP.NET Core HTTP-сервер (а не gRPC), отдаёт страницы браузеру и выступает gRPC-**клиентом** к остальным сервисам. См. [[modules/backend-web]].

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
- Точка входа: `MainActivity` → `BarkCloudTheme { RootNavGraph() }`; DI — `BarkCloudApplication` (см. [[modules/android-app]])
- Сборка: `./gradlew assembleDebug` или через Android Studio

## iOS-клиент

- Корень проекта: `Ios/BarkCloud/BarkCloud.xcodeproj`
- Bundle id: `com.barkfluff.BarkCloud`
- Точка входа: `@main BarkCloudApp` → `RootView` (см. [[modules/ios-app]])
- Сборка: `xcodebuild` или через Xcode
