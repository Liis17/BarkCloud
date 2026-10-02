# gRPC API — Identity

Parent: [[index]] · Module: [[modules/backend-identity]] · Proto: [[modules/shared-proto]]

Файл: `Shared/BarkCloud.Proto/identity_api.proto`
Namespace C#: `BarkCloud.Proto.Identity`
Package: `barkcloud.identity`

## Сервис: `IdentityApi` (клиентский)

| RPC | Реализовано? | Назначение |
|-----|--------------|-----------|
| `Auth(AuthRequest) → AuthResponse` | ✅ | Авторизация login + password. Пароль проверяется **первым**; при включённой 2FA без `otp_code` → `OtpCodeNeedException` (email-2FA: письмо с кодом уходит только после верного пароля). Email-код: 5 мин, 5 попыток, одноразовый; повторная отправка не чаще раза в 60 с (внутри окна письмо не шлётся, ответ тот же `OtpCodeNeed`). Просроченный/использованный код → `NotValidOtpCodeException`; чтобы получить новый — повторить `Auth` без `otp_code`. **Лимиты (F14):** 10 попыток за 15 мин на аккаунт (до проверки пароля; дальше `PasswordAttemptsExceededException` даже при верном пароле, сброс — успешным входом) и 60 за 15 мин на источник (`TooManyRequestsException`). См. [[modules/backend-identity]] |
| `FastAuth(FastAuthRequest) → AuthResponse` | ❌ | Объявлен в proto, **нет handler-а** в `Features/` |
| `CreateToken(CreateTokenRequest) → CreateTokenResponse` | ✅ | Обновить access по refresh |
| `CreateAccount(CreateAccountRequest) → CreateAccountResponse` | ✅ | Регистрация. Лимит по источнику и по получателю письма (cooldown 60 с, 5 / ч) → `TooManyRequestsException` |
| `ConfirmAccount(ConfirmAccountRequest) → ConfirmAccountResponse` | ✅ | Подтвердить аккаунт. 5 попыток на код (дальше `ConfirmationCodeIncorrectException` даже при верном коде), лимит по источнику |
| `GetActiveSessions` / `RemoveActiveSession` | ✅ | Сессии |
| `EnableOtpVerification` / `ConfirmOtpVerification` / `DisableOtpVerification` / `ListOtpVerification` | ✅ | Управление 2FA. `Enable(Authenticator)` требует `password` (+ `current_otp_code`, если Authenticator уже включён) и сохраняет секрет как **ожидающий** (10 мин) — действующий не меняется до `Confirm` с кодом нового секрета. `Disable(Email)` требует `password`; `Disable(Authenticator)` — `otp_code`. Неверный пароль → `InvalidPasswordException`; не более 5 попыток пароля за 15 мин, дальше `PasswordAttemptsExceededException` (даже при верном пароле). См. [[modules/backend-identity]] |
| `ResetPassword` / `ConfirmResetPassword` / `SetPassword` | ✅ | Пароль. `ConfirmResetPassword(reset_id, otp_code, new_password, optional revoke_other_sessions)` — **одним вызовом** проверяет код, ставит новый пароль и (по умолчанию) завершает остальные сессии, возвращает токены; отдельный `SetPassword` после сброса не нужен. Новый пароль не может совпадать с текущим (и в `SetPassword`, и в сбросе). **Лимиты (F14):** `ResetPassword` — по источнику (10 / ч) и письма на аккаунт (cooldown 60 с, 5 / ч; при отказе письмо не шлётся, возвращается `ResetId` действующего запроса); `ConfirmResetPassword` — 5 попыток на `reset_id` (потом `NotValidOtpCodeException` даже при верном коде) и 30 / 15 мин на источник; `SetPassword` — 5 неверных `old_password` за 15 мин → `PasswordAttemptsExceededException`. См. [[modules/backend-identity]] |
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

См. `Shared/BarkCloud.Shared.Exceptions/Identity/` ([[modules/shared-exceptions]]) — 32 исключения, в т.ч. `TooManyRequestsException` (лимит по источнику/получателю/коду, метаданные `x-retry-after-seconds`, F14), `InvalidLoginOrPasswordException`, `InvalidOldPasswordException`, `InvalidPasswordException`, `PasswordAttemptsExceededException`, `NewPasswordSameAsOldException`, `NewPasswordRequiredException`, `InvalidRefreshTokenException`, `OtpCodeNeedException`, `EmailExistException`, `UsernameReservedException`, `XAppInfoIsRequiedException`, `XDeviceNameIsRequiredException`, `XOsNameIsRequiredException`, а также WebAuthn: `NoWebAuthnCredentialsException`, `WebAuthnChallengeExpiredException`, `WebAuthnVerificationFailedException`.

## Связанные потоки

- `Logout` / `RemoveActiveSession` → `SessionRevokedEvent` в RabbitMQ → потребляется `Users`, `Files`, `Identity` (`SessionRevokedConsumer.cs` в каждом)
- Подтверждения по email → `EmailNotification` через `NotificationQueueSender.cs`
