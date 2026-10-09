# Backend — Identity

Parent: [[Index]] · See also: [[Backend/SessionRevocation]], [[Backend/IdentityNotificationOutbox]], [[Backend/AccountDeletionOutbox]], [[Shared/SharedLibraries]]

## Назначение

Сервис учётных данных и сессий: регистрация и вход, access/refresh tokens, пароль, TOTP и email OTP, WebAuthn, восстановление доступа и управление сессиями.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Identity/Host/IdentityApiService.cs` | `IdentityApiService` | Пользовательские RPC |
| `Backend/BarkCloud.Identity/Persistence/Contexts/IdentityContext.cs` | `IdentityContext` | Таблицы аутентификации, токенов и уведомлений |
| `Backend/BarkCloud.Identity/Persistence/Services/ConfirmationCodesStorage.cs` | `ConfirmationCodesStorage` | Резервирование попытки и условное погашение кода регистрации |
| `Backend/BarkCloud.Identity/Host/IdentityServerApiService.cs` | `IdentityServerApiService` | Служебные RPC |
| `Backend/BarkCloud.Identity/Services/JwtService.cs` | `JwtService` | Выпуск access и service JWT |
| `Backend/BarkCloud.Identity/Services/SessionIssuer.cs` | `SessionIssuer` | Создание сессии и регистрация устройства |
| `Backend/BarkCloud.Identity/Persistence/Services/RefreshTokensStorage.cs` | `RefreshTokensStorage` | Refresh-токены и атомарная запись отзывов |
| `Backend/BarkCloud.Identity/Services/AuthRateLimiter.cs` | `AuthRateLimiter` | Ограничение частоты auth/OTP-запросов |
| `Backend/BarkCloud.Identity/Infrastructure/RegistrationPolicy.cs` | `RegistrationPolicy` | Проверка флага регистрации |
| `Shared/BarkCloud.Proto/identity_api.proto` | `IdentityApi`, `IdentityServerApi` | gRPC-контракт |

## Публичные контракты

В `IdentityApi` без пользовательского токена доступны `Auth`, `CreateToken`, `CreateAccount`, `ConfirmAccount`, `ResetPassword`, `ConfirmResetPassword` и вход WebAuthn (`BeginWebAuthnAssertion`, `CompleteWebAuthnAssertion`). Управление сессиями, паролем, OTP и привязанными WebAuthn-ключами требует policy `User`; она принимает user или service JWT.

`IdentityServerApi` целиком защищён service JWT. Его методы обслуживают административное чтение/удаление сессий и OTP, создание сессии пользователя и принудительную смену пароля. Отдельный service-only контракт `SessionRevocationApi` описан в [[Backend/SessionRevocation]].

`FastAuth` объявлен в proto, но `IdentityApiService` не переопределяет этот RPC; обработчик gRPC не предоставлен.

`ConfirmResetPassword` принимает `reset_id`, `otp_code`, `new_password` и optional `revoke_other_sessions`; отсутствующее поле трактуется как `true`. `SetPassword` требует `old_password`, если у пользователя уже есть сохранённый пароль. Остальные поля определены в `Shared/BarkCloud.Proto/identity_api.proto`.

## Основные потоки и данные

- `IdentityContext` хранит пароли, свойства аутентификации, коды подтверждения, запросы сброса, refresh-токены, WebAuthn-ключи/challenge'и, счётчики попыток, отзывы сессий и `PendingNotifications`. Rate-limit счётчики для IP/аккаунта и уведомлений сохраняются в `AuthAttemptCounters`.
- Новые пароли хешируются BCrypt с work factor 12; при проверке также поддерживаются сохранённые legacy SHA-256 хеши.
- `Auth` и assertion WebAuthn используют общий transactional core `SessionIssuer`: до транзакции получается геолокация, затем callback локальной проверки (если задан), замена refresh на устройстве и создание нового refresh, выпуск access JWT с отложенной успешной telemetry и вставка `SuccessfulLogin` фиксируются одним коммитом. Assertion сначала проверяет криптографическую подпись вне транзакции, а challenge и счётчик credential условно погашаются/обновляются в ней. Access содержит user, device и session claims; `x-session-id` равен ID строки refresh-токена.
- `Auth` резервирует попытки email-кода до входа, а затем потребляет ту же issuance (purpose, code, `IssuedAt`, expiry) внутри транзакции входа. Для WebAuthn callback использует условное удаление assertion challenge по ID/type/expiry и CAS по предыдущему счётчику; отсутствие challenge или изменённый/удалённый credential откатывает всю локальную сессию.
- Сессия регистрирует устройство через `UsersServerApi` после локального коммита. Сохраняется существующее best-effort поведение; deadline/cancellation семантика этого RPC этой работой не меняется.
- Смена пароля, принудительная смена, подтверждённый reset, вход и включение/выключение 2FA фиксируют соответствующую локальную мутацию, токены/отзывы сессий и outbox-событие одной транзакцией. TOTP проверяется до транзакции; email-коды заранее проверяются и резервируются по immutable issuance, а внутри транзакции условно потребляется та же issuance. Метрики успешной операции и соответствующие логи выполняются после коммита. Первоначальная установка пароля по-прежнему не отправляет письмо.
- `ConfirmAccount` вызывает `Users.ConfirmUser` до локальной транзакции; Users handler идемпотентно снимает `IsDraft`. После RPC Identity условно удаляет ожидаемую действующую Registration issuance, создаёт refresh и `SuccessfulRegistration` в одном локальном коммите. При локальном rollback код остаётся доступен для повтора, и клиент повторно вызывает Users до истечения кода/лимита попыток. Это не distributed transaction: Users может уже считать аккаунт подтверждённым, а фоновой компенсации или recovery worker нет.
- Успешные входы и уведомления о смене пароля/метода 2FA ставятся в локальную очередь доставки; см. [[Backend/IdentityNotificationOutbox]].
- `RegistrationPolicy` читает `Features:RegistrationEnabled` через Configuration при регистрации и использует последнее известное значение, если вызов недоступен.

## Зависимости и взаимодействия

Identity использует PostgreSQL, `BarkCloud.GrpcServer`, `BarkCloud.Shared.Identity`, `BarkCloud.Shared.Auth`, `BarkCloud.Shared.Exceptions` и `BarkCloud.Shared.Queue`. Через gRPC обращается к Configuration и Users; письма публикует в RabbitMQ, WebAuthn обслуживает Fido2. Сервис принимает `UserDeleted` и удаляет связанные с пользователем данные Identity; см. [[Backend/Users]].

## Ограничения и важные детали

`CreateToken` проверяет наличие и срок действия refresh-записи и выпускает access для её `UserId`, `DeviceId` и ID; refresh в этом RPC не ротируется. Access lifetime задаёт `JwtSettings:ExpiryMinutes`; текущий refresh lifetime — 9999 дней, клиенты должны ориентироваться на `expiration_date` в ответе. Имена JWT claims, типы токенов и UTF-8 обработка секрета описаны в [[Shared/SharedLibraries]].
