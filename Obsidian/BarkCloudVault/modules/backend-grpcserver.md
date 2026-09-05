# Backend — GrpcServer

Parent: [[index]]

## Назначение

Общий **хост-фреймворк** для всех Backend-микросервисов. Не запускаемое приложение — это библиотека с расширениями для `WebApplicationBuilder`/`IServiceCollection`, поднимающая gRPC, Serilog, метрики, перехватчики, опциональный TLS. Каждый сервис (`Configuration`, `Identity`, `Users`, `Files`) подключает её в своём `Program.cs`.

## Расположение

`Backend/BarkCloud.GrpcServer/`

## Файлы

### Корневые
- `WebApplicationBuilderExtensions.cs` — расширения для `WebApplicationBuilder` (регистрация Kestrel/gRPC/Serilog и т.п.)
- `ServiceCollectionExtensions.cs` — расширения DI
- `SerilogExtensions.cs` — настройка Serilog с экспортом в Seq; если `Seq:ServerUrl` не
  задан, используется Docker-адрес `http://cloud-seq:5341` из production compose
- `ServerExceptionInterceptor.cs` — gRPC-интерсептор для маппинга .NET-исключений на gRPC-статусы (использует [[modules/shared-exceptions]])

### Settings
- `RunSettings.cs` — настройки запуска (порты и т.д.)
- `TlsSettings.cs` — настройки TLS

### Metrics
- `MetricsCollector.cs` — сбор метрик
- `MetricsReporterService.cs` — фоновая публикация метрик

### Tracker (request context)
- `IRequestContextAccessor.cs`, `RequestContext.cs` — контекст текущего запроса (аналог HttpContextAccessor)
- `RequestContextInterceptor.cs` — gRPC-интерсептор, наполняющий `RequestContext` из метаданных запроса

### XAuth (авторизация)
- `XAuthExtensions.cs` — DI/middleware для авторизации
- `UserContext.cs` — текущий пользователь (claims + device)
- `TokenRevocationCache.cs` — in-memory кэш отозванных сессий по ключу `{userId}:{deviceId}`. **Учитывает время**: `Revoke` запоминает момент отзыва, `IsRevoked(userId, deviceId, tokenIssuedAt)` считает токен отозванным только если его `iat` ≤ момента отзыва. Поэтому повторный логин с тем же устройством (новый токен с iat > отзыва) валиден сразу, а не ждёт 60 мин. `OnTokenValidated` (`XAuthExtensions.cs`) берёт `iat` из `JsonWebToken`/`JwtSecurityToken` (fail-safe `MinValue` → «отозван»).
- `TokenRevocationCleanupService.cs` — фоновая очистка записей по `ExpiresAt` (когда старые токены устройства уже истекли)

## Что подключает каждый сервис

Типовой `Program.cs` микросервиса:
1. `builder.AddBarkCloudGrpcServer(...)` (или аналог) — Kestrel + gRPC + Serilog
2. Регистрация интерсепторов: `ServerExceptionInterceptor`, `RequestContextInterceptor`, XAuth
3. Подключение метрик
4. Регистрация своих gRPC-сервисов из `Host/`

## Зависимости

- Использует: `BarkCloud.Shared.Exceptions`, `BarkCloud.Shared.Identity`, ASP.NET Core gRPC, Serilog
- Используется: всеми Backend-микросервисами
