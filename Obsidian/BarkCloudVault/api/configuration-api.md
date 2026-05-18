# gRPC API — Configuration

Parent: [[index]] · Module: [[modules/backend-configuration]] · Proto: [[modules/shared-proto]]

Файл: `Shared/BarkCloud.Proto/configuration_api.proto`
Namespace C#: `BarkCloud.Proto.Configuration`
Package: `barkcloud.configuration`

## Сервис: `ConfigurationApi`

Служебный API — вызывается другими микросервисами при старте.

| RPC | Назначение |
|-----|-----------|
| `GetConfiguration(GetConfigurationRequest) → GetConfigurationResponse` | Получить настройки сервиса по `service_id` |
| `UpdateConfiguration(UpdateConfigurationRequest) → UpdateConfigurationResponse` | Обновить значение настройки |
| `GetReservedNames(GetReservedNamesRequest) → GetReservedNamesResponse` | Список зарезервированных юзернеймов |
| `AddReservedName(AddReservedNameRequest) → AddReservedNameResponse` | Добавить зарезервированное имя |
| `UpdateReservedName(UpdateReservedNameRequest) → UpdateReservedNameResponse` | Обновить |
| `DeleteReservedName(DeleteReservedNameRequest) → DeleteReservedNameResponse` | Удалить |

## Поля

- `GetConfigurationRequest.service_id: int32` — идентификатор сервиса (см. [[modules/shared-identity]] · `ServiceId.cs`)

## Использование

- `Users` дёргает `GetReservedNames` через [[modules/backend-users]] · `ReservedUsernamesService.cs`
- Все микросервисы дёргают `GetConfiguration` при старте через переменную `CONFIGURATION_SERVICE_URL`
