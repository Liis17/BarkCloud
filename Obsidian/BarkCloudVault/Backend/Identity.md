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
| `Backend/BarkCloud.Identity/Services/JwtService.cs` | `JwtService` | Выпуск access и сервисных JWT (RS256, приватный ключ только у Identity) |
| `Backend/BarkCloud.Identity/Host/ServiceCredentialsApiService.cs` | `ServiceCredentialsApiService` | Обмен секрета сервиса на короткоживущий сервисный JWT (реестр `SERVICE_CREDENTIALS`) |
| `Backend/BarkCloud.Identity/Program.cs` | `Main` | Загрузка настроек и регистрация server-to-server клиентов |
| `Backend/BarkCloud.Identity/Services/SessionIssuer.cs` | `SessionIssuer` | Создание сессии и регистрация устройства |
| `Backend/BarkCloud.Identity/Persistence/Services/RefreshTokensStorage.cs` | `RefreshTokensStorage` | Refresh-токены и атомарная запись отзывов |
| `Backend/BarkCloud.Identity/Services/AuthRateLimiter.cs` | `AuthRateLimiter` | Ограничение частоты auth/OTP-запросов |
| `Backend/BarkCloud.Identity/Services/TotpVerificationService.cs` | `TotpVerificationService` | Проверка секрета и поиск принятого временного шага TOTP |
| `Backend/BarkCloud.Identity/Services/ReauthenticationService.cs` | `ReauthenticationService` | Одноразовые разрешения текущей сессии для управления passkey |
| `Backend/BarkCloud.Identity/Infrastructure/RegistrationPolicy.cs` | `RegistrationPolicy` | Проверка флага регистрации |
| `Shared/BarkCloud.Proto/identity_api.proto` | `IdentityApi`, `IdentityServerApi` | gRPC-контракт |

## Публичные контракты

В `IdentityApi` без пользовательского токена доступны `Auth`, `CreateToken`, `CreateAccount`, `CreateAccountWithPassword`, `ConfirmAccount`, `ConfirmAccountWithPassword`, `ResetPassword`, `ConfirmResetPassword` и вход WebAuthn (`BeginWebAuthnAssertion`, `CompleteWebAuthnAssertion`). Управление сессиями, паролем, OTP и привязанными WebAuthn-ключами требует policy `User`; она принимает user или service JWT. Добавление и удаление passkey дополнительно требует одноразового разрешения свежей повторной аутентификации в текущей сессии.

`IdentityServerApi` целиком защищён service JWT. Его методы обслуживают административное чтение/удаление сессий и OTP, создание сессии пользователя и принудительную смену пароля. Отдельный service-only контракт `SessionRevocationApi` описан в [[Backend/SessionRevocation]].

`ListOtpVerification` возвращает оба флага 2FA как `false`, если у пользователя ещё нет записи настроек. Чтение не создаёт запись и передаёт cancellation token в хранилище; ошибки хранилища передаются вызывающему коду.

`FastAuth` объявлен в proto, но `IdentityApiService` не переопределяет этот RPC; обработчик gRPC не предоставлен.

`ConfirmResetPassword` принимает `reset_id`, `otp_code`, `new_password`, optional `revoke_other_sessions` и optional `reset_passkeys`; отсутствие каждого поля трактуется как `true`. Восстановление создаётся только по email-коду. Если включён Authenticator, подтверждение требует email-код и TOTP; это требование проверяется заново под блокировкой владельца. TOTP-only сброс отклоняется при создании и подтверждении, включая ранее выданные reset ID. При отключённой доставке email восстановление выполняет администратор через `ForceSetPasswordServer`. Повторная отправка обоих кодов перепроверяет их целиком. `SetPassword` требует `old_password`, если у пользователя уже есть сохранённый пароль, и сохраняет существующие passkey. Первоначальная установка пароля при уже привязанных passkey требует свежего подтверждения одним из этих ключей. Остальные поля определены в `Shared/BarkCloud.Proto/identity_api.proto`.

Новые `CreateAccountWithPassword` и `ConfirmAccountWithPassword` требуют непустой первоначальный пароль минимум из 6 символов. Поле `initial_password` добавлено как optional к общим request-сообщениям; старые RPC его игнорируют.

`Auth` возвращает существующий OTP-required error code с metadata `x-otp-method=authenticator|email`; при email OTP также передаются остаток реального resend cooldown в `x-otp-retry-after-seconds` и статус `x-otp-mail-status=sent|existing`. Эти данные появляются только после успешной проверки пароля; если включены оба метода, первым используется authenticator. `ResetPassword` возвращает нейтральный случайный `reset_id` и задержку и для неизвестной учётной записи, и для account-mail throttle без активного reset.

## Основные потоки и данные

- `IdentityContext` хранит пароли, свойства аутентификации, коды подтверждения, запросы сброса, refresh-токены, WebAuthn-ключи/challenge'и, счётчики попыток, отзывы сессий и `PendingNotifications`. Rate-limit счётчики для IP/аккаунта и уведомлений сохраняются в `AuthAttemptCounters`.
- Активный TOTP-секрет имеет поколение и последний принятый шаг. `TotpVerificationService` проверяет код с `VerificationWindow.RfcSpecifiedNetworkDelay`; атомарное условное обновление шага допускает только более новый код для текущих секрета, поколения и включённого Authenticator. Погашение шага фиксируется в одной транзакции с защищаемым действием; проверенный при настройке код сразу становится погашенным при активации секрета.
- Новые пароли хешируются BCrypt с work factor 12; при проверке также поддерживаются сохранённые legacy SHA-256 хеши.
- `Auth` и assertion WebAuthn используют общий transactional core `SessionIssuer`: до транзакции получается геолокация, затем callback локальной проверки (если задан), замена refresh на устройстве и создание нового refresh, выпуск access JWT с отложенной успешной telemetry и вставка `SuccessfulLogin` фиксируются одним коммитом. Assertion сначала проверяет криптографическую подпись вне транзакции, а challenge и счётчик credential условно погашаются/обновляются в ней. Access содержит user, device и session claims; `x-session-id` равен ID строки refresh-токена.
- `Auth` резервирует попытки email-кода до входа, а затем потребляет ту же issuance (purpose, code, `IssuedAt`, expiry) внутри транзакции входа. Для WebAuthn callback использует условное удаление assertion challenge по ID/type/expiry и CAS по предыдущему счётчику; отсутствие challenge или изменённый/удалённый credential откатывает всю локальную сессию.
- `Auth`, `CompleteWebAuthnAssertion` и `CreateSessionForUserServer` передают cancellation token gRPC-вызова в mediator; их handlers передают его в `SessionIssuer`.
- После локального коммита `SessionIssuer` регистрирует устройство через `UsersServerApi` с UTC deadline и исходным caller token. Регистрация ограничена отдельным бюджетом 10 секунд и локальным `WaitAsync`; `AsyncUnaryCall` освобождается после ответа, отмены или таймаута. Таймаут, `DeadlineExceeded`, `Unavailable`, `Cancelled` и другие ошибки регистрации при неотменённом caller логируются и не блокируют выдачу уже созданной пары токенов.
- Отмена caller до RPC не запускает регистрацию; отмена во время неё прекращает ожидание. Обе границы расположены до метрик `sessions_created` и `auth_login_success`, но после коммита: сохранённые refresh-токен, сессия и письмо `SuccessfulLogin` в outbox не откатываются и не компенсируются.
- Смена пароля, принудительная смена, подтверждённый reset, вход и включение/выключение 2FA фиксируют соответствующую локальную мутацию, токены/отзывы сессий и outbox-событие одной транзакцией. Криптографическая проверка TOTP предшествует транзакции, а погашение шага выполняется вместе с защищаемой операцией; email-коды заранее проверяются и резервируются по immutable issuance, а внутри транзакции условно потребляется та же issuance. Метрики успешной операции и соответствующие логи выполняются после коммита. Первоначальная установка пароля по-прежнему не отправляет письмо.
- `BeginReauthentication` выдаёт разрешение на пять минут для пользователя, действующей сессии, действия и, при удалении, конкретного passkey. В БД хранится только хеш случайного токена; парольный вариант требует активные факторы аккаунта, а passkey-вариант использует assertion challenge только с ключами владельца и `UserVerification.Required`. Email-код reauth применяется при включённой только email-2FA; его выдача и письмо через notification outbox атомарны. Разрешение одноразовое; завершение регистрации passkey атомарно погашает разрешение и challenge.
- Каждая смена пароля увеличивает `SecurityStateVersion`, очищает незавершённые reauth/passkey-регистрации и сбрасывает ожидающую настройку 2FA. Это выполняется и при reset, и при `ForceSetPasswordServer`, независимо от флагов отзыва сессий и удаления passkey. Принудительная смена пароля может отдельно удалить все passkey и отозвать сессии; она не создаёт новую пользовательскую сессию.
- Подтверждение reset атомарно меняет пароль, погашает reset, применяет оба выбора (`reset_passkeys`, `revoke_other_sessions`), отзывает старые сессии до выпуска новой и ставит уведомление в outbox. Обычная смена пароля passkey не удаляет.
- `CreateAccountWithPassword` при включённой почте создаёт обычную Registration issuance и не сохраняет первоначальный пароль до подтверждения кода. При отключённой почте Identity под блокировкой владельца фиксирует hash пароля и refresh в локальной транзакции до `Users.ConfirmUser`; если внешний вызов не удался, защищённый draft завершает только повторный новый RPC с верным паролем. Восстановление защищённого draft проверяет пароль под той же блокировкой и лимитом login-попыток, затем подтверждает Users до создания нового refresh. Обычные Create RPC и legacy Confirm отказывают для такого draft до изменения профиля или выдачи токена.
- `ConfirmAccount` и `ConfirmAccountWithPassword` берут transaction-scoped PostgreSQL advisory lock по owner ID и повторно проверяют, что у draft нет сохранённого пароля. Legacy RPC сохраняет поведение для непарольных draft; вариант `WithPassword` дополнительно вставляет первоначальный hash. Оба варианта условно погашают исходную Registration issuance, создают refresh и `SuccessfulRegistration` в одной Identity-транзакции. Локальный rollback оставляет код доступным для повтора; Users вызывается с 10-секундным deadline и исходным cancellation token до локального коммита. Это не distributed transaction: Users может уже считать аккаунт подтверждённым, а фоновой компенсации или recovery worker нет.
- Успешные входы и уведомления о смене пароля/метода 2FA ставятся в локальную очередь доставки; см. [[Backend/IdentityNotificationOutbox]].
- `RegistrationPolicy` читает `Features:RegistrationEnabled` через Configuration при регистрации и использует последнее известное значение, если вызов недоступен.

## Зависимости и взаимодействия

Identity использует PostgreSQL, `BarkCloud.GrpcServer`, `BarkCloud.Shared.Identity`, `BarkCloud.Shared.Auth`, `BarkCloud.Shared.Exceptions` и `BarkCloud.Shared.Queue`. Через gRPC обращается к Configuration и Users; письма публикует в RabbitMQ, WebAuthn обслуживает Fido2. `Program` использует `IdentityService:Host` для регистрации клиента получения сервисного JWT, которым Identity авторизует server-to-server вызов к Users. Сервис принимает `UserDeleted` и удаляет связанные с пользователем данные Identity, включая reauth-разрешения и WebAuthn challenge'и; см. [[Backend/Users]].

## Ограничения и важные детали

- Android Credential Manager origin строится из `BarkCloud:AndroidCertificateSha256Fingerprint` (Compose: `BARKCLOUD_ANDROID_CERT_SHA256_FINGERPRINT`): SHA-256 fingerprint сертификата преобразуется в `android:apk-key-hash:<base64url>`. Отпечаток должен совпадать с подписью APK и с ответом Web `/.well-known/assetlinks.json`. Builder и `Backend/sample.env` по умолчанию задают сертификат официального APK; при собственной подписи нужен её публичный отпечаток.

`CreateToken` проверяет наличие и срок действия refresh-записи и выпускает access для её `UserId`, `DeviceId` и ID; refresh в этом RPC не ротируется. Access lifetime задаёт `JwtSettings:ExpiryMinutes`; текущий refresh lifetime — 9999 дней, клиенты должны ориентироваться на `expiration_date` в ответе. Имена JWT claims, типы токенов и UTF-8 обработка секрета описаны в [[Shared/SharedLibraries]].

RS256 приватный ключ используется только Identity; валидаторы читают соответствующий публичный ключ. При переходе с прежнего алгоритма подписи старые access JWT могут перестать проходить валидацию, но миграция Configuration не удаляет сохранённые refresh-записи Identity. Пока Identity DB и согласованная RSA-пара сохранены, действующая refresh-сессия может получить новый access без повторного входа.

Отмена выдачи сессии проверяется после завершения регистрации устройства и до начала enqueue. Уже начатая запись уведомления в outbox использует `CancellationToken.None` и не прерывается этим caller token. Сохранение refresh-токена и регистрация устройства по RPC не объединены SQL-транзакцией.
