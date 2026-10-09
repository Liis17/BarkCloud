# Backend — Identity

Parent: [[Index]] · See also: [[Backend/SessionRevocation]], [[Backend/IdentityNotificationOutbox]], [[Backend/AccountDeletionOutbox]], [[Shared/SharedLibraries]]

## Назначение

Сервис учётных данных и сессий: регистрация и вход, access/refresh tokens, пароль, TOTP и email OTP, WebAuthn, восстановление доступа и управление сессиями.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Identity/Host/IdentityApiService.cs` | `IdentityApiService` | Пользовательские RPC |
| `Backend/BarkCloud.Identity/Persistence/Contexts/IdentityContext.cs` | `IdentityContext` | Таблицы аутентификации, токенов и уведомлений |
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
- `Auth` и assertion WebAuthn используют `SessionIssuer`: на устройстве заменяется прежний refresh, создаётся новый refresh-токен, а `CreateToken` выпускает access JWT. Access содержит user, device и session claims; `x-session-id` равен ID строки refresh-токена.
- `Auth`, `CompleteWebAuthnAssertion` и `CreateSessionForUserServer` передают cancellation token gRPC-вызова в mediator; их handlers передают его в `SessionIssuer`.
- После сохранения refresh-токена и выпуска access JWT `SessionIssuer` регистрирует устройство через `UsersServerApi` с UTC deadline и исходным caller token. Регистрация ограничена отдельным бюджетом 10 секунд и локальным `WaitAsync`; `AsyncUnaryCall` освобождается после ответа, отмены или таймаута. Таймаут, `DeadlineExceeded`, `Unavailable`, `Cancelled` и другие ошибки регистрации при неотменённом caller логируются и не блокируют выдачу уже созданной пары токенов.
- Отмена caller до RPC не запускает регистрацию; отмена во время неё прекращает ожидание. Обе границы расположены до постановки письма об успешном входе и метрик успешной выдачи сессии. Уже сохранённый refresh-токен и созданная сессия не откатываются и не компенсируются.
- В транзакции подтверждения сброса расходуется reset, сохраняется новый пароль и выдаётся текущему устройству новая сессия; при `revoke_other_sessions=true` также отзываются прежние refresh. Детали транзакции и удаления аккаунта — в [[Backend/AccountDeletionOutbox]].
- Успешные входы и уведомления о смене пароля/метода 2FA ставятся в локальную очередь доставки; см. [[Backend/IdentityNotificationOutbox]].
- `RegistrationPolicy` читает `Features:RegistrationEnabled` через Configuration при регистрации и использует последнее известное значение, если вызов недоступен.

## Зависимости и взаимодействия

Identity использует PostgreSQL, `BarkCloud.GrpcServer`, `BarkCloud.Shared.Identity`, `BarkCloud.Shared.Auth`, `BarkCloud.Shared.Exceptions` и `BarkCloud.Shared.Queue`. Через gRPC обращается к Configuration и Users; письма публикует в RabbitMQ, WebAuthn обслуживает Fido2. Сервис принимает `UserDeleted` и удаляет связанные с пользователем данные Identity; см. [[Backend/Users]].

## Ограничения и важные детали

`CreateToken` проверяет наличие и срок действия refresh-записи и выпускает access для её `UserId`, `DeviceId` и ID; refresh в этом RPC не ротируется. Access lifetime задаёт `JwtSettings:ExpiryMinutes`; текущий refresh lifetime — 9999 дней, клиенты должны ориентироваться на `expiration_date` в ответе. Имена JWT claims, типы токенов и UTF-8 обработка секрета описаны в [[Shared/SharedLibraries]].

Отмена выдачи сессии проверяется после завершения регистрации устройства и до начала enqueue. Уже начатая запись уведомления в outbox использует `CancellationToken.None` и не прерывается этим caller token. Сохранение refresh-токена и регистрация устройства по RPC не объединены SQL-транзакцией.
