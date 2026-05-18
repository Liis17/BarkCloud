# Backend — Configuration

Parent: [[index]] · See also: [[api/configuration-api]]

## Назначение

Центральный сервис хранения настроек. При старте каждого другого микросервиса (`identity`, `users`, `files`) тот обращается сюда за своей конфигурацией. Также управляет списком зарезервированных юзернеймов (`ReservedNames`), которые `Users` использует при регистрации.

## Расположение

`Backend/BarkCloud.Configuration/`

## Файлы

- `Program.cs` — точка входа
- `Domain/ConfigurationItem.cs` — доменная сущность (key/value-настройка)
- `Host/ConfigurationApiService.cs` — реализация gRPC `ConfigurationApi`
- `Infrastructure/ConfigurationContext.cs` — EF Core DbContext
- `Infrastructure/ConfigurationContextFactory.cs` — фабрика контекста (для EF Tools)
- `Infrastructure/ConfigurationDefaultsPopulator.cs` — заливка дефолтных значений
- `Infrastructure/ConfigurationStorage.cs` — слой доступа к данным
- `Persistence/Migrations/20260518172647_InitialCreate.cs` — единственная миграция
- `Dockerfile`, `Dockerfile.slim`
- `appsettings.json`, `appsettings.Development.json`

## Features (vertical slices)

Каждая фича — `XxxCommand.cs` + `XxxCommandHandler.cs`:

- `GetConfiguration` — отдать настройки для service_id
- `UpdateConfiguration` — обновить значение
- `GetReservedNames` / `AddReservedName` / `UpdateReservedName` / `DeleteReservedName` — CRUD по зарезервированным юзернеймам

## gRPC API

Один публичный сервис — `ConfigurationApi`. Используется только серверными микросервисами, клиенту не выставляется. См. [[api/configuration-api]].

## Зависимости

- Использует: `BarkCloud.Proto`, `BarkCloud.GrpcServer`, EF Core, PostgreSQL
- Используется: всеми остальными микросервисами при старте

## Окружение (compose)

ENV переменные: `CONFIGURATION_HOST`, `CONFIGURATION_DATABASE`, `CONFIGURATION_USERNAME`, `CONFIGURATION_PASSWORD`, `CONFIGURATION_PORT` (см. [[structure/infrastructure]]).
