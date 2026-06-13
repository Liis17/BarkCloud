# gRPC API — Identity

Parent: [[index]] · Module: [[modules/backend-identity]] · Proto: [[modules/shared-proto]]

Файл: `Shared/BarkCloud.Proto/identity_api.proto`
Namespace C#: `BarkCloud.Proto.Identity`
Package: `barkcloud.identity`

## Сервис: `IdentityApi` (клиентский)

| RPC | Реализовано? | Назначение |
|-----|--------------|-----------|
| `Auth(AuthRequest) → AuthResponse` | ✅ | Авторизация login + password |
| `FastAuth(FastAuthRequest) → AuthResponse` | ❌ | Объявлен в proto, **нет handler-а** в `Features/` |
| `CreateToken(CreateTokenRequest) → CreateTokenResponse` | ✅ | Обновить access по refresh |
| `CreateAccount(CreateAccountRequest) → CreateAccountResponse` | ✅ | Регистрация |
| `ConfirmAccount(ConfirmAccountRequest) → ConfirmAccountResponse` | ✅ | Подтвердить аккаунт |
| `GetActiveSessions` / `RemoveActiveSession` | ✅ | Сессии |
| `EnableOtpVerification` / `ConfirmOtpVerification` / `DisableOtpVerification` / `ListOtpVerification` | ✅ | Управление 2FA |
| `ResetPassword` / `ConfirmResetPassword` / `SetPassword` | ✅ | Пароль |
| `Logout(LogoutRequest) → LogoutResponse` | ✅ | Завершить сессию (триггерит `SessionRevokedEvent`) |
| `BeginWebAuthnRegistration` / `CompleteWebAuthnRegistration` | ✅ | Привязка ключа безопасности (под токеном) |
| `BeginWebAuthnAssertion` / `CompleteWebAuthnAssertion` | ✅ | Вход по ключу (passwordless, **публичные** как `Auth`) → выдают `AuthResponse` |
| `ListWebAuthnCredentials` / `RemoveWebAuthnCredential` | ✅ | Список/удаление ключей (под токеном) |

## Сервис: `IdentityServerApi` (служебный)

Все RPC реализованы:

- `ListOtpVerificationServer`, `DisableOtpVerificationServer`
- `GetActiveSessionsServer`, `RemoveActiveSessionServer`
- `CreateSessionForUserServer` — выпуск сессии от имени сервиса
- `ForceSetPasswordServer` — принудительная установка пароля админом

## Типизированные ошибки

См. `Shared/BarkCloud.Shared.Exceptions/Identity/` ([[modules/shared-exceptions]]) — 25 исключений, в т.ч. `InvalidLoginOrPasswordException`, `InvalidRefreshTokenException`, `OtpCodeNeedException`, `EmailExistException`, `UsernameReservedException`, `XAppInfoIsRequiedException`, `XDeviceNameIsRequiredException`, `XOsNameIsRequiredException`, а также WebAuthn: `NoWebAuthnCredentialsException`, `WebAuthnChallengeExpiredException`, `WebAuthnVerificationFailedException`.

## Связанные потоки

- `Logout` / `RemoveActiveSession` → `SessionRevokedEvent` в RabbitMQ → потребляется `Users`, `Files`, `Identity` (`SessionRevokedConsumer.cs` в каждом)
- Подтверждения по email → `EmailNotification` через `NotificationQueueSender.cs`
