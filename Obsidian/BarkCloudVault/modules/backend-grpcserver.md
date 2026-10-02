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
- `SerilogExtensions.cs` — настройка Serilog с экспортом в Seq и выводом в stdout во всех
  окружениях; если `Seq:ServerUrl` не задан, используется Docker-адрес
  `http://cloud-seq:5341` из production compose. Поэтому стартовые ошибки доступны через
  `docker logs`, даже если процесс завершился до отправки событий в Seq
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
- `SourceIpResolver.cs` — доверенный адрес источника для лимитов попыток (`RequestContext.SourceIp`, F14): валидный `X-Real-IP`, иначе адрес соединения; IPv4-mapped → IPv4, IPv6 → /64. Клиентские `x-ip-address` и `X-Forwarded-For` не используются (они остаются в `IpAddress` для писем и логов)

### XAuth (авторизация)
- `XAuthExtensions.cs` — DI/middleware для авторизации. Ключ проверки JWT строится сразу при `AddXAuth` из `JwtSecret.GetKeyBytes` (UTF-8, [[modules/shared-identity]]), а не лениво в `AddJwtBearer`: пустой секрет или короче 16 байт останавливает Identity/Users/Files/Torrent на старте с понятным сообщением (F23)
- `UserContext.cs` — текущий пользователь (claims + device + `IssuedAt` из клейма `iat`, `MinValue` если нет); `IssuedAt` нужен долгим стримам для повторной проверки отзыва (`TorrentApiService.StreamProgress`)
- `TokenRevocationCache.cs` — локальный hot-path кэш: `Revoke(userId, deviceId, revokedAt, expiresAt)` принимает исходное время Identity и сохраняет максимальные время/expiry. `IsRevoked` по-прежнему проверяет `iat <= RevokedAt`; `OnTokenValidated` читает `iat` из JWT (fallback `MinValue`). См. [[modules/session-revocation]].
- `RevocationSyncService.cs` — полный снимок с ретраями в `StartAsync` до открытия Kestrel, затем poll 5 с с перекрытием 1 мин и очисткой кэша; заменяет удалённый `TokenRevocationCleanupService`.

- `IRevocationFeed.cs` — `RevocationBatch`/`SessionRevocation` и общий интерфейс источника.
- `GrpcRevocationFeed.cs` — service-only клиент Identity, deadline 10 с; серверный контракт генерируется здесь единожды ([[api/session-revocation-api]]). Identity использует DB-feed.

## Генерация protobuf

Единственное место генерации контрактов `configuration_api.proto` и `session_revocation_api.proto` — этот проект, оба с `GrpcServices="Both"`. Сервисы, которые ссылаются на GrpcServer, свои копии не генерируют: Configuration реализует `ConfigurationApi.ConfigurationApiBase` из GrpcServer (как Identity — `SessionRevocationApiBase`), поэтому одноимённые CLR-типы не дублируются в связанных сборках и `CS0436` не возникает (аудит Q01). Новый контракт, который нужен нескольким связанным сборкам, генерировать здесь же, а не повторно в потребителе.

## Что подключает каждый сервис

Типовой `Program.cs` микросервиса:
1. `builder.AddBarkCloudGrpcServer(...)` (или аналог) — Kestrel + gRPC + Serilog
2. Регистрация интерсепторов: `ServerExceptionInterceptor`, `RequestContextInterceptor`, XAuth
3. Подключение метрик
4. Регистрация своих gRPC-сервисов из `Host/`

## Зависимости

- Использует: `BarkCloud.Shared.Exceptions`, `BarkCloud.Shared.Identity`, ASP.NET Core gRPC, Serilog
- Используется: всеми Backend-микросервисами
- Версии gRPC/protobuf выровнены: `Google.Protobuf 3.36.1`, `Grpc.Tools` и runtime-пакеты `Grpc.*` — `2.83.0`.
