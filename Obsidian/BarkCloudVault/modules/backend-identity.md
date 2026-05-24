# Backend — Identity

Parent: [[index]] · See also: [[api/identity-api]] · [[modules/shared-identity]] · [[modules/shared-queue]]

## Назначение

Сервис идентификации: регистрация, авторизация, выдача JWT/refresh-токенов, 2FA (TOTP + email-OTP), управление активными сессиями, сброс пароля, отправка email-уведомлений с подтверждением.

## Расположение

`Backend/BarkCloud.Identity/`

## Файлы

### Domain
- `AuthUserProperty.cs`
- `ConfirmationCode.cs`, `ConfirmationCodeType.cs`
- `OtpType.cs`
- `RefreshToken.cs`
- `ResetPassword.cs`
- `UserPassword.cs`

### Host (gRPC)
- `IdentityApiService.cs` — клиентский `IdentityApi`
- `IdentityServerApiService.cs` — серверный `IdentityServerApi`

### Services
- `JwtService.cs` — выпуск/валидация JWT
- `PasswordHasher.cs` — хеширование паролей
- `RefreshTokenGenerator.cs` — генерация refresh-токенов
- `CodeGenerator.cs` — генерация кодов подтверждения

### Infrastructure
- `LocationClient.cs`, `LocationClientExtensions.cs` — определение IP-локации
- `IpLocation.cs` — DTO результата
- `NotificationQueueSender.cs` — отправка `EmailNotification` через RabbitMQ ([[modules/shared-queue]])

### Settings
- `JwtSettings.cs` — issuer, audience, ключ, lifetime

### Consumers
- `SessionRevokedConsumer.cs` — наполняет `TokenRevocationCache` по `SessionRevokedEvent`
- `UserDeletedConsumer.cs` — по `UserDeleted` (из [[modules/backend-users]]) отзывает все сессии (удаляет refresh-токены + публикует `SessionRevokedEvent` по каждому устройству) и удаляет пароль/2FA-свойства/запросы сброса/коды подтверждения

### Persistence
- `Contexts/IdentityContext.cs`, `IdentityContextFactory.cs`
- `Services/AuthPropertiesStorage.cs`
- `Services/ConfirmationCodesStorage.cs`
- `Services/PasswordsStorage.cs`
- `Services/RefreshTokensStorage.cs`
- `Services/ResetPasswordsStorage.cs`
- `Exceptions/OtpNotCreatedException.cs` (локальный)
- `Exceptions/RefreshTokenNotFoundException.cs` (локальный)
- `Migrations/` — 8 миграций:
  - `20250408213248_IdentityInitial`
  - `20250503180927_AddConfirmationCodes`
  - `20250508184250_AddOtp`
  - `20250509001710_AddEmailOtp`
  - `20250601191802_FixLastEmailCodeProps`
  - `20250613165357_AddResetAndPasswords`
  - `20260207120000_RenameDeviceNameToDeviceId`
  - `20260507005955_SecurityHardening`

## Features (реализованные)

| Feature | Назначение |
|---------|-----------|
| `Auth` | Авторизация (login + password) |
| `CreateAccount` / `ConfirmAccount` | Регистрация + подтверждение |
| `CreateToken` | Обновить access по refresh |
| `Logout` | Завершить сессию |
| `EnableOtpVerification` / `ConfirmOtpVerification` / `DisableOtpVerification` / `ListOtpVerification` | Управление 2FA |
| `ResetPassword` / `ConfirmResetPassword` / `SetPassword` | Сброс/установка пароля |
| `GetActiveSessions` / `RemoveActiveSession` | Клиентское управление сессиями |
| `CreateSessionForUserServer` | Создание сессии служебно |
| `ForceSetPasswordServer` | Принудительная установка пароля (админ) |
| `GetActiveSessionsServer`, `RemoveActiveSessionServer`, `DisableOtpVerificationServer`, `ListOtpVerificationServer` | Серверные аналоги |

> **Не реализовано** (но объявлено в `identity_api.proto`): `FastAuth`. Папки/файлов в `Features/` нет.

## gRPC API

См. [[api/identity-api]].

## Зависимости

- Использует: `BarkCloud.Proto`, `BarkCloud.GrpcServer`, `BarkCloud.Shared.Identity`, `BarkCloud.Shared.Auth`, `BarkCloud.Shared.Exceptions`, `BarkCloud.Shared.Queue`, EF Core, JWT
- Используется: всеми клиентами (Android), `Users`/`Files`-сервисами для валидации токенов

## Окружение

`ASPNETCORE_ENVIRONMENT`, `CONFIGURATION_SERVICE_URL`. БД/JWT-настройки берутся из [[modules/backend-configuration]] при старте.
