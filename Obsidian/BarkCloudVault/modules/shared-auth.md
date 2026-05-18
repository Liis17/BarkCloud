# Shared — Auth

Parent: [[index]]

## Назначение

gRPC-интерсепторы для **исходящих** запросов между сервисами (и клиент→сервер). Прокидывают стандартные заголовки/метаданные: JWT, идентификатор устройства/приложения, ОС, IP.

## Расположение

`Shared/BarkCloud.Shared.Auth/`

## Файлы

| Файл | Назначение |
|------|-----------|
| `MetadataKeys.cs` | Имена ключей метаданных (`x-device`, `x-device-id`, `x-app`, `x-os`, `x-ip`, токен) |
| `JwtClientInterceptor.cs` | Прокидывает JWT-токен в метаданных исходящих gRPC-запросов |
| `XAppClientInterceptor.cs` | Прокидывает `X-App` (информация о приложении) |
| `XDeviceClientInterceptor.cs` | Прокидывает `X-Device` (имя/название устройства) |
| `XDeviceIdInterceptor.cs` | Прокидывает идентификатор устройства |
| `XIpClientInterceptor.cs` | Прокидывает IP клиента |
| `XOsClientInterceptor.cs` | Прокидывает ОС |

## Парное использование

Серверная сторона разбирает эти метаданные через `RequestContextInterceptor` из [[modules/backend-grpcserver]]. Часть исключений из [[modules/shared-exceptions]] завязана на отсутствие этих заголовков (`XAppInfoIsRequiedException`, `XDeviceNameIsRequiredException`, `XOsNameIsRequiredException`).

## Зависимости

- Использует: gRPC core (Grpc.Net.Client)
- Используется: всеми клиентами (включая межсервисные вызовы) и для аутентифицированных вызовов
