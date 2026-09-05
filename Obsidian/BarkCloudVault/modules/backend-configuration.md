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
- `Infrastructure/ConfigurationSeed.cs` — эталонный список всех ожидаемых ключей (`Section`/`Key`/`ServiceId`), включая SMTP-поля `Email:*` для Notification и общий флаг `Features:RegistrationEnabled`
- `Infrastructure/ConfigurationDefaultsPopulator.cs` — заливка дефолтных значений. `EnsureSeedAsync` при **каждом** старте сверяет таблицу с `ConfigurationSeed` и досевает только недостающие ключи (по тройке `Section/Key/ServiceId`), без дубликатов — новые ключи доезжают и в уже существующую БД. `PopulateDefaultsAsync` заполняет **пустые** записи дефолтами. Внутренние Docker-адреса используют имена production compose: `cloud-rabbitmq`, `cloud-seq`, `cloud-minio`, а межсервисные адреса — `cloud-identity`, `cloud-users`, `cloud-files`, `cloud-torrent`. SMTP-поля `Email:*` (Notification) и `ExternalEndpoint:Host` (Identity/Users/Files/Torrent) берутся из env (`.env`): email опционален (пусто → не трогаем, режим без почты), внешние адреса обязательны — вне Development пустой env даёт `InvalidOperationException` при старте (проброшен в `Program.cs`, контейнер падает). В Development внешние адреса фолбэчат на `https://{subdomain}.example.com`. Конструктор получает эти значения из `Program.cs` (`EMAIL_*`, `EXTERNAL_{IDENTITY,USERS,FILES,TORRENT}_HOST`) + флаг `requireExternalEndpoints = !IsDevelopment()`
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

Для авто-заполнения БД на чистом старте сервис `configuration` также получает:
- `EMAIL_HOST` / `EMAIL_PORT` / `EMAIL_SENDER_EMAIL` / `EMAIL_SENDER_PASSWORD` — SMTP (опционально; пусто → без почты).
- `EXTERNAL_IDENTITY_HOST` / `EXTERNAL_USERS_HOST` / `EXTERNAL_FILES_HOST` / `EXTERNAL_TORRENT_HOST` — внешние адреса сервисов для клиентов (обязательны вне Development).

Эти ключи генерит [[modules/tools-builder]] в `.env` и продублированы в `Backend/sample.env`.

## Режим без почты (Features:EmailEnabled)

`GetConfigurationCommandHandler` подмешивает в ответ **всем** сервисам вычисляемый ключ
`Features:EmailEnabled` (под `ServiceId.Unknown`, поэтому доезжает до Identity/Web/всех через их `LoadConfiguration`).
Значение считается `ConfigurationStorage.IsEmailConfiguredAsync()`: `true`, только если **все 4** поля
`Email:Host/Port/SenderEmail/SenderPassword` (под `ServiceId.Notification`) непусты; иначе `false`.
Ключ **не хранится** в БД — всегда свежий на старте сервиса (смена SMTP требует рестарта Identity/Web).
`ConfigurationDefaultsPopulator` заполняет секцию `Email` из env (`EMAIL_*`): если все 4 заданы — почта
включается, если env пуст — поля остаются пустыми и деплой работает в режиме без почты (дефолт).
Потребители флага — [[modules/backend-identity]] и [[modules/backend-web]].

## Запрет регистрации (Features:RegistrationEnabled)

`Features:RegistrationEnabled` хранится в БД как общий ключ `ServiceId.Unknown`. Миграция `AddRegistrationEnabledFlag` и `ConfigurationSeed` добавляют значение по умолчанию `true`, чтобы существующие инстансы не потеряли возможность регистрации после обновления.

Флаг меняется из Web-настроек через `ConfigurationApi.UpdateConfiguration`. При `false` [[modules/backend-identity]] запрещает создание и подтверждение новых аккаунтов для всех клиентов; Web дополнительно скрывает UI регистрации на странице входа.
