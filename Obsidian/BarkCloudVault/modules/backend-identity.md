# Backend — Identity

Parent: [[index]] · See also: [[api/identity-api]] · [[modules/shared-identity]] · [[modules/shared-queue]]

## Назначение

Сервис идентификации: регистрация, авторизация, выдача JWT/refresh-токенов, 2FA (TOTP + email-OTP), управление активными сессиями, сброс пароля, отправка email-уведомлений с подтверждением.

## Расположение

`Backend/BarkCloud.Identity/`

## Файлы

### Domain
- `AuthUserProperty.cs` (+ `WebAuthnUserHandle` — непубличный user handle WebAuthn)
- `ConfirmationCode.cs`, `ConfirmationCodeType.cs`
- `OtpType.cs`
- `RefreshToken.cs`
- `ResetPassword.cs`
- `UserPassword.cs`
- `WebAuthnCredential.cs` — привязанный ключ FIDO2 (CredentialId, PublicKey, SignatureCounter, AaGuid)
- `WebAuthnChallenge.cs` — временный challenge между begin/complete (TTL 5 мин)

### Host (gRPC)
- `IdentityApiService.cs` — клиентский `IdentityApi`
- `IdentityServerApiService.cs` — серверный `IdentityServerApi`

### Services
- `JwtService.cs` — выпуск/валидация JWT
- `PasswordHasher.cs` — хеширование паролей
- `RefreshTokenGenerator.cs` — генерация refresh-токенов
- `CodeGenerator.cs` — генерация кодов подтверждения
- `SessionIssuer.cs` — общий выпуск сессии (refresh+access, регистрация устройства, уведомление); используется входом по ключу (хвост `AuthCommandHandler`)
- `Fido2` (пакет `Fido2` 4.0.1) регистрируется в `Program.cs` из `WebAuthn:RpId/ServerName/Origins`

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
- `Services/WebAuthnStorage.cs` — ключи + challenge'и + user handle
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
  - `20260613125810_AddWebAuthn` — таблицы `WebAuthnCredentials`/`WebAuthnChallenges` + `WebAuthnUserHandle`

## Features (реализованные)

| Feature | Назначение |
|---------|-----------|
| `Auth` | Авторизация (login + password) |
| `BeginWebAuthnRegistration` / `CompleteWebAuthnRegistration` | Привязка ключа безопасности (под токеном) |
| `BeginWebAuthnAssertion` / `CompleteWebAuthnAssertion` | Вход по ключу без пароля (публичные) → `SessionIssuer` |
| `ListWebAuthnCredentials` / `RemoveWebAuthnCredential` | Список/удаление ключей |
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

## Режим без почты (email-less)

Читает `Features:EmailEnabled` (вычисляет [[modules/backend-configuration]]) через `IConfiguration.EmailEnabled()` ([[modules/backend-grpcserver]]). При `false`:
- `NotificationQueueSender.SendNotification` **ничего не публикует** — глушит все 12 точек отправки `EmailNotification` разом (очередь не копится, сервис Notification можно остановить).
- `CreateAccountCommandHandler`: после создания черновика **сразу** `ConfirmUser` + выдаёт refresh (`CreateAccountResponse.refresh_token`), минуя код подтверждения. Письмо не шлётся. В режиме с почтой — прежний двухшаговый путь.
- `ResetPasswordCommandHandler` (email-OTP) и `EnableOtpVerificationCommandHandler` (тип Email) бросают `EmailServiceDisabledException`. Сценарии на **Authenticator/TOTP** не затронуты.
- `AuthCommandHandler` намеренно не меняли (enforcement 2FA не трогаем; в свежем email-less деплое email-OTP включить нельзя).

## Вход по ключу безопасности (WebAuthn / FIDO2, passwordless)

Модель **username-first passwordless**: пользователь вводит логин → сервер отдаёт `allowCredentials` → касание ключа. Пароль остаётся для регистрации/привязки первого ключа/восстановления (fallback пароль + Email-OTP).

- Сервер — единственный держатель ключей и место валидации (`Fido2` из пакета `Fido2`). Web/Drive — тонкие релеи. Те же методы переиспользуют будущие Android/iOS.
- WebAuthn-данные передаются в proto **JSON-строками** (options/attestation/assertion) — `Fido2` сериализует их штатно.
- **RP ID** = домен сервера, выводится [[modules/backend-configuration]] из `ExternalEndpoint:Host` Identity (`EXTERNAL_IDENTITY_HOST`); `Origins = https://<домен>`. Конфиг `WebAuthn:RpId/ServerName/Origins`. Требует доменный хост + TLS (не голый IP).
- Begin/Complete-assertion — **публичные** (без токена, как `Auth`); registration/list/remove — под токеном пользователя.
- Клиенты: [[modules/backend-web]] (релей + `navigator.credentials`), [[modules/windows-drive]] (`webauthn.dll` через DSInternals, только вход).
