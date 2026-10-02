# Backend — Identity

Parent: [[index]] · See also: [[api/identity-api]] · [[modules/shared-identity]] · [[modules/shared-queue]]

## Назначение

Сервис идентификации: регистрация, авторизация, выдача JWT/refresh-токенов, 2FA (TOTP + email-OTP), управление активными сессиями, сброс пароля, отправка email-уведомлений с подтверждением.

## Расположение

`Backend/BarkCloud.Identity/`

## Файлы

### Domain
- `AuthUserProperty.cs` (+ `WebAuthnUserHandle` — непубличный user handle WebAuthn; + поля email-challenge: `LastEmailAuthCode`, `EmailAuthCodePurpose`, `EmailAuthCodeIssuedAt`, `EmailAuthCodeExpiresAt`, `EmailAuthCodeAttempts` — см. «Email-код 2FA (F07)»)
- `EmailAuthCodePurpose.cs` — назначение email-кода: `Login`, `EnableEmailOtp`
- `ConfirmationCode.cs` (+ `Attempts` — попытки ввода, F14), `ConfirmationCodeType.cs`
- `AuthAttemptCounter.cs` — счётчик попыток/отправок в окне по произвольному ключу (F14)
- `OtpType.cs`
- `RefreshToken.cs`
- `RevokedSession.cs` — долговечный отзыв по времени и срок жизни записи ([[modules/session-revocation]])
- `ResetPassword.cs` (+ `OtpAttempts` — попытки ввода кода, F14)
- `UserPassword.cs`
- `PendingNotification.cs` — письмо в очереди доставки (outbox, F19) — [[modules/notification-outbox]]
- `WebAuthnCredential.cs` — привязанный ключ FIDO2 (CredentialId, PublicKey, SignatureCounter, AaGuid)
- `WebAuthnChallenge.cs` — временный challenge между begin/complete (TTL 5 мин)

### Host (gRPC)
- `IdentityApiService.cs` — клиентский `IdentityApi`
- `IdentityServerApiService.cs` — серверный `IdentityServerApi`
- `SessionRevocationApiService.cs` — feed отзывов для сервисных JWT ([[api/session-revocation-api]])

### Services
- `JwtService.cs` — выпуск/валидация JWT; байты ключа подписи берёт из `JwtSecret.GetKeyBytes` (UTF-8, общий с проверкой в `AddXAuth`, F23 — [[modules/shared-identity]])
- `DbRevocationFeed.cs` — читает активные отзывы в отдельном scope; полный снимок и дельта
- `PasswordHasher.cs` — хеширование паролей
- `RefreshTokenGenerator.cs` — генерация refresh-токенов
- `CodeGenerator.cs` — генерация кодов подтверждения
- `SessionIssuer.cs` — единый выпуск сессии (refresh+access, регистрация устройства, уведомление о входе через outbox): `IssueAsync(userId, ct)` — вход пользователя (`AuthCommandHandler`, WebAuthn), `IssueAsync(userId, SessionDevice, ct)` — устройство задано явно (`CreateSessionForUserServer`). `auth_login_success` считает только первая перегрузка; `sessions_created` — обе
- `AuthLimits.cs` — политика лимитов попыток и рассылки (F14); `AuthRateLimiter.cs` (`IAuthRateLimiter`, scoped) — применение политики по `RequestContext.SourceIp` / аккаунту / получателю
- `PasswordChangedNotifier.cs` — ставит в outbox письмо «Пароль успешно изменен» (`NotificationType.PasswordChanged`) и не бросает; общий хвост `SetPassword` (при смене, не при первичной установке) и `ConfirmResetPassword`
- `INotificationOutbox.cs`, `NotificationOutbox.cs`, `NotificationPayload.cs`, `NotificationOutboxWorker.cs` — outbox уведомлений и фоновая доставка (F19), см. [[modules/notification-outbox]]
- `Fido2` (пакет `Fido2` 4.0.1) регистрируется в `Program.cs` из `WebAuthn:RpId/ServerName/Origins`

### Infrastructure
- `LocationClient.cs`, `LocationClientExtensions.cs` — определение IP-локации; запрос ограничен `LocationClient.RequestTimeout` (2 с), по таймауту — «-» (F19)
- `RegistrationPolicy.cs` — runtime-проверка `Features:RegistrationEnabled` через Configuration API с fallback на стартовую конфигурацию
- `IpLocation.cs` — DTO результата
- `NotificationQueueSender.cs` — публикация `EmailNotification` в RabbitMQ ([[modules/shared-queue]]); вызывается воркером outbox и синхронными письмами с кодами (`CreateAccount`, `ResetPassword`, email-код `Enable`/`Auth`)

### Settings
- `JwtSettings.cs` — issuer, audience, ключ, lifetime

### Consumers
- `UserDeletedConsumer.cs` — по `UserDeleted` (из [[modules/backend-users]]) отзывает все сессии (удаляет refresh-токены и сохраняет `RevokedSession` в одной транзакции) и удаляет пароль/2FA-свойства/запросы сброса/коды подтверждения

### Persistence
- `Contexts/IdentityContext.cs`, `IdentityContextFactory.cs`
- `Services/AuthPropertiesStorage.cs`
- `Services/AttemptCountersStorage.cs` — атомарный `TryReserve(key, max, window)` / `Reset` для `AuthAttemptCounters` (F14)
- `Services/ConfirmationCodesStorage.cs`
- `Services/PasswordsStorage.cs`
- `Services/RefreshTokensStorage.cs` — `RevokeSession`, `RevokeSessionSafe`, `RevokeAllSessions`: атомарные refresh-delete + отзыв; очистка истёкших записей ([[modules/session-revocation]])
- `Services/ResetPasswordsStorage.cs`
- `Services/WebAuthnStorage.cs` — ключи + challenge'и + user handle
- `Exceptions/OtpNotCreatedException.cs` (локальный)
- `Exceptions/RefreshTokenNotFoundException.cs` (локальный)
- `Migrations/`:
  - `20250408213248_IdentityInitial`
  - `20250503180927_AddConfirmationCodes`
  - `20250508184250_AddOtp`
  - `20250509001710_AddEmailOtp`
  - `20250601191802_FixLastEmailCodeProps`
  - `20250613165357_AddResetAndPasswords`
  - `20260207120000_RenameDeviceNameToDeviceId`
  - `20260507005955_SecurityHardening`
  - `20260613125810_AddWebAuthn` — таблицы `WebAuthnCredentials`/`WebAuthnChallenges` + `WebAuthnUserHandle`
  - `20261001190754_EmailAuthCodeChallenge` — аддитивно: `EmailAuthCodePurpose`/`IssuedAt`/`ExpiresAt`/`Attempts` в `AuthUserProperties` (F07; `LastEmailAuthCode` не тронут)

  - `20261001194141_AddRevokedSessions` — таблица отзывов и индексы по `RevokedAt`/`ExpiresAt` (F10)
  - `20261002112945_AddPendingNotifications` — таблица `PendingNotifications` (outbox уведомлений) и индекс по `NextAttemptAt` (F19)
  - `20261002101816_AddAuthAttemptCounters` — таблица счётчиков попыток (F14)
  - `20261002102704_AddOtpAttempts` — `ResetPasswords.OtpAttempts`, `ConfirmationCodes.Attempts` (F14)

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

## Смена и сброс пароля (F02)

**`SetPassword`** (под токеном): при уже заданном пароле обязателен верный `old_password`, иначе `InvalidOldPasswordException`; новый пароль равный старому → `NewPasswordSameAsOldException`. Пустой хеш (аккаунт без строки в `UserPasswords`) = первичная установка при регистрации — старый не нужен, письмо не шлётся.

**`ConfirmResetPassword`** (публичный) — атомарная установка пароля одним вызовом: `reset_id`, `otp_code`, **`new_password`**, `optional revoke_other_sessions` (не передано = `true`). Порядок шагов (безопасный отказ при сбое):
1. проверка заголовков, reset (не найден/использован/истёк), OTP;
2. пустой `new_password` → `NewPasswordRequiredException`; совпадение с текущим хешем → `NewPasswordSameAsOldException` (**до** захвата reset — код можно использовать повторно);
3. `ResetPasswordsStorage.TryApprove` — атомарный `UPDATE … WHERE IsApproved=false`; `false` → `ResetIdHasIsApprovedException` (параллельные подтверждения: успех один);
4. при `revoke_other_sessions`: `RefreshTokensStorage.RevokeAllSessions(userId, currentDeviceId, ct)` атомарно удаляет все прежние refresh и записывает access-отзывы по устройствам, **кроме текущего** (`iat <= RevokedAt`; новый токен текущего устройства не должен попасть под отзыв);
5. `UpdateUserPasswordHash`; 6. выдача refresh+access текущему устройству; 7. письмо через `PasswordChangedNotifier` — постановка в outbox после commit; ошибка не отменяет результат, доставка — воркером ([[modules/notification-outbox]]).

После F11 шаги 3–6 выполняются в одной явной транзакции общего scoped `IdentityContext`: хеш вычисляется заранее, письмо отправляется после commit. Ошибка создания токенов или отмена откатывает пароль, отзывы, refresh и расходование reset. `UserDeletedConsumer` также объединяет отзыв сессий и всю существующую очистку в одну транзакцию. Подробности — [[modules/transactional-outbox]].

Хеш пароля при сбросе **не очищается** (`ClearUserPasswordHash` удалён) — окна «пароль пуст, любая сессия ставит свой» нет. Известный остаток: для того же `deviceId` access-токен проживёт до `JwtSettings.ExpiryMinutes`; legacy-аккаунты с ранее очищенным хешем сохраняют «первичную установку» без старого пароля до первой установки пароля.

## Email-код 2FA (F07)

**Порядок `Auth`:** валидация заголовков → поиск пользователя → **проверка пароля** (неверный → `FailedLogin`-письмо + `InvalidLoginOrPasswordException`) → настройки 2FA → TOTP / email-код → выдача токенов. Пароль проверяется первым: без него нельзя ни получить код, ни узнать, что у аккаунта включена 2FA. Протокол без состояния — на втором шаге (с `otp_code`) пароль проверяется повторно.

**Challenge** хранится в `AuthUserProperty` (один активный на пользователя; новая выдача заменяет прежний). Логика — в `AuthPropertiesStorage`, константы приватные: TTL **5 мин**, **5 попыток**, cooldown повторной выдачи **60 с**.
- `TryIssueEmailAuthCode(userId, purpose, code)` — условный `ExecuteUpdate`; `false`, если код **того же** `purpose` выдан < 60 с назад (письмо не шлётся, прежний код остаётся). Если строки `AuthUserProperty` нет — создаёт. Cooldown привязан к пользователю, не к IP.
- `TryConsumeEmailAuthCode(userId, purpose, code)` — по образцу `ResetPasswordsStorage.TryApprove`: (1) атомарно списывает попытку (`UPDATE … WHERE Purpose, код не NULL, ExpiresAt > now, Attempts < 5`), (2) сравнивает `CryptographicOperations.FixedTimeEquals`, (3) атомарно обнуляет challenge (`WHERE LastEmailAuthCode = код`) — из параллельных запросов успех получает один. Просроченный/использованный/исчерпавший попытки/чужого назначения код → `false` → хендлеры бросают прежний `NotValidOtpCodeException` (новых исключений нет, клиенты не менялись).
- Назначения: `Login` (`AuthCommandHandler`) и `EnableEmailOtp` (`EnableOtpVerificationCommandHandler` выдаёт, `ConfirmOtpVerificationCommandHandler` расходует) — код входа не подходит для привязки и наоборот.
- Если `TryIssue` вернул `false`, хендлер входа всё равно бросает `OtpCodeNeedException` (клиент остаётся на вводе кода), но письмо не отправляется; кнопка «отправить ещё раз» заработает через минуту.
- Старые коды после миграции имеют `ExpiresAt = NULL` и считаются просроченными. Код хранится открытым текстом (как `ResetPassword.OtpCode`).

Лимиты попыток входа/OTP по IP и аккаунту, троттлинг `FailedLogin` и лимит попыток email-кода сброса пароля добавлены в F14 — см. «Лимиты попыток и рассылки (F14)».

## Управление 2FA: ожидающий секрет и повторная аутентификация (F08)

**Жизненный цикл Authenticator.** `EnableOtpVerification(Authenticator)` не трогает действующие `OtpSecret`/`OtpEnabled`: новый секрет пишется в `AuthUserProperty.PendingOtpSecret` (+ `PendingOtpSecretExpiresAt`, TTL **10 мин**, константа `PendingSecretLifetime` в хендлере) через `SetPendingOtpSecret`, `SelectedOtpType = Authenticator`. `ConfirmOtpVerification` проверяет код **по ожидающему секрету** (нет секрета/истёк → shared `OtpNotCreatedException` — начать настройку заново; код действующего приложения новый секрет не подтверждает) и вызывает `ActivatePendingOtpSecret(userId, проверенный секрет)` — условный `ExecuteUpdate … WHERE PendingOtpSecret = @секрет`, который одним UPDATE копирует pending в `OtpSecret`, ставит `OtpEnabled = true` и очищает pending; `false` (секрет успели заменить параллельным Enable) → `NotValidOtpCodeException`. Закрытый QR-экран или повторное открытие настройки вход прежним приложением не ломают. `DisableOtp` дополнительно очищает pending. `GetOtpSecretKey` остаётся — его использует `ConfirmResetPassword`.

**Повторная аутентификация** (проверки идут до любых записей):
- `EnableOtpVerification(Authenticator)` — текущий пароль `password` (иначе `InvalidPasswordException`, метрика `otp_setup_failed_invalid_password`); если Authenticator уже активен — ещё и `current_otp_code` против действующего секрета (`NotValidOtpCodeException`, `otp_setup_failed_invalid_otp`). Пароль проверяется первым, поэтому без него нельзя перебирать TOTP. Аккаунт без хеша пароля отклоняется (`PasswordHasher.VerifyPassword(…, null)` = `false`) — сначала `SetPassword`.
- `DisableOtpVerification(Email)` — `password`; метрика `otp_disable_failed`. `Disable(Authenticator)` — по-прежнему только TOTP (`otp_code`).
- `EnableOtpVerification(Email)` — без изменений (код уходит владельцу на почту; его challenge — см. F07).

Новая ошибка `InvalidPasswordException` («Неверный пароль») — в отличие от `InvalidOldPasswordException` (смена пароля) не говорит про «старый».

**Лимит попыток пароля.** Проверка пароля — в `ReauthPasswordVerifier` (`Services/`, scoped; используют `Enable` и `Disable`): пустой пароль → `InvalidPasswordException` без расхода попытки; затем `IAuthPropertiesStorage.TryReserveReauthPasswordAttempt` **до** проверки хеша — один условный `ExecuteUpdate` (окно **15 мин**, **5 попыток**; окно открывается первой попыткой, `ReauthPasswordAttempts` / `ReauthPasswordWindowEndsAt` в `AuthUserProperty`), поэтому параллельный перебор не превысит лимит; нет строки `AuthUserProperty` — создаётся сразу с первой попыткой. Лимит исчерпан → `PasswordAttemptsExceededException` («Слишком много неверных попыток ввода пароля. Повторите позже») даже при верном пароле; верный пароль → `ResetReauthPasswordAttempts`. Метрика остаётся прежней (`otp_setup_failed_invalid_password` / `otp_disable_failed`). Побочный эффект: владелец сессии может на 15 мин заблокировать себе только эти операции (вход не затрагивается). Миграции: `AddPendingOtpSecret` (2 nullable-колонки), `AddReauthPasswordAttempts` (`int` + nullable `timestamptz`). Лимит попыток входа, старого пароля в `SetPassword` и перебора TOTP закрыт в F14 (см. ниже); не охвачен отзыв остальных сессий при смене 2FA.

## Запрет регистрации (Features:RegistrationEnabled)

`CreateAccountCommandHandler` и `ConfirmAccountCommandHandler` вызывают `RegistrationPolicy.EnsureRegistrationEnabledAsync` до создания/подтверждения пользователя. При `false` бросается `RegistrationDisabledException`: новые аккаунты не создаются и ранее начатая регистрация не подтверждается.

Политика читает флаг из [[modules/backend-configuration]] на каждый регистрационный запрос, поэтому переключатель в Web-настройках действует без перезапуска Identity. Если Configuration временно недоступен, используется последнее известное значение с fallback на стартовую конфигурацию.

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

## Уведомления вне критического пути (F19)

Основное изменение (пароль, сессия, подтверждение аккаунта, смена 2FA) больше не зависит от писем. После коммита хендлер вызывает `INotificationOutbox.EnqueueAsync`, который только вставляет строку в `PendingNotifications` и не бросает; адрес из Users, геолокацию и публикацию в RabbitMQ делает `NotificationOutboxWorker` с повторами и паузой до 24 ч. Через outbox идут письма `FailedLogin`, `SuccessfulLogin`, `SuccessfulRegistration`, `PasswordChanged`, `PasswordChangedByAdmin`, `TwoFactorMethodChanged`. Письма с кодами (регистрация, сброс пароля, email-код 2FA и входа) остались синхронными: там ошибка должна дойти до клиента.

Вход паролем, по ключу и серверное создание сессии выпускают токены одним `SessionIssuer`: сбой Users/очереди одинаково не влияет на результат любого способа. Ожидание геолокации ограничено 2 с. Подробности, гарантии, метрики и диагностика — [[modules/notification-outbox]].

## Долговечный отзыв сессий (F10)

Identity — источник истины для отзывов; все реплики загружают снимок до старта Kestrel и обновляют кэш примерно каждые 5 с. `Logout`, удаление сессии, сброс пароля и `UserDeletedConsumer` используют storage без событий отзыва. При сбросе все прежние refresh удаляются, текущее устройство исключается только из access-отзыва. Подробности, ограничения секундного `iat`, изменения lifetime и порядок обновления — [[modules/session-revocation]].

## Лимиты попыток и рассылки (F14)

**Источник запроса.** IP-лимиты считаются по `RequestContext.SourceIp` (`SourceIpResolver` в [[modules/backend-grpcserver]]): валидный `X-Real-IP`, иначе адрес соединения; IPv4-mapped → IPv4, IPv6 → префикс /64. Клиентский `x-ip-address` и `X-Forwarded-For` не используются (nginx лишь дописывает к последнему свой адрес, первый задаёт клиент) — они остаются только в `RequestContext.IpAddress` для писем и логов. nginx перезаписывает `X-Real-IP` значением `$remote_addr`; Web вызывает Identity напрямую и сам передаёт адрес браузера метаданными `x-real-ip` (`DeviceInfo.ToMetadata`). Модель доверия держится на том, что Identity недоступен снаружи в обход nginx (порты в compose не публикуются) — см. [[structure/infrastructure]]. Нет адреса → предупреждение в лог и IP-лимит пропускается.

**Хранилище.** Таблица `AuthAttemptCounters` (`Key` ≤200, `Count`, `WindowEndsAt`); `AttemptCountersStorage.TryReserve` — условный `ExecuteUpdate` (окно открывается первой попыткой, отказ не увеличивает счётчик и возвращает остаток окна), нет строки — вставка с обработкой гонки уникального ключа; просроченные более часа строки удаляются при вставке новой. Ключи: `{scope}:ip:{ip}`, `{scope}:{userId}`, для регистрации — `{scope}:{sha256(email)}`. Счётчики, привязанные к самой записи, лежат в ней: `ResetPassword.OtpAttempts`, `ConfirmationCode.Attempts` (условный `ExecuteUpdate` до сравнения кода; не зависят от IP).

| Операция | Ключ | Лимит / окно | Отказ |
|---|---|---|---|
| `Auth` | IP | 60 / 15 мин | `TooManyRequestsException` |
| `Auth` после поиска юзера | аккаунт | 10 / 15 мин, до bcrypt/TOTP/выдачи email-кода; сброс полным успешным входом | `PasswordAttemptsExceededException` |
| письмо `FailedLogin` | аккаунт | 1 / 15 мин | письмо не шлётся |
| `ResetPassword` | IP; аккаунт | 10 / ч; cooldown 60 с и 5 / ч на email-код | письмо не шлётся, отдаётся `ResetId` действующего запроса (нет такого → `TooManyRequestsException`) |
| `ConfirmResetPassword` | IP; `reset_id` | 30 / 15 мин; 5 попыток (email и TOTP) | `TooManyRequestsException`; `NotValidOtpCodeException` |
| `CreateAccount` | IP; получатель | 10 / ч; cooldown 60 с и 5 / ч (после создания черновика, только при включённой почте) | `TooManyRequestsException` |
| `ConfirmAccount` | IP; `code_id` | 30 / 15 мин; 5 попыток | `TooManyRequestsException`; `ConfirmationCodeIncorrectException` |
| `Begin/CompleteWebAuthnAssertion` | IP | 60 / 15 мин | `TooManyRequestsException` |
| TOTP под токеном (`ConfirmOtp`, `Disable(Authenticator)`, `Enable` при замене) | аккаунт | 10 / 15 мин | `TooManyRequestsException` |
| `SetPassword` (old_password) | аккаунт | общий reauth-счётчик F08, 5 / 15 мин; пустой старый пароль попытку не тратит | `PasswordAttemptsExceededException` |

`TooManyRequestsException` (`Shared.Exceptions/Identity`, код `8F2B6D41-5A93-4C7E-B0D8-1E4A7C9F3B26`) несёт метаданные `x-retry-after-seconds` (клиентский интерсептор пересоздаёт исключение по коду без метаданных; Web показывает `Status.Detail`). Метрики: `auth_login_failed_locked`, `password_reset_confirmation_failed_attempts_exceeded`, `account_confirmation_failed_attempts_exceeded`, `password_reset_mail_throttled`, `account_confirmation_mail_throttled`, `password_change_failed_attempts_exceeded`.

**Компромиссы.** Окно-блокировка аккаунта (как F08): знающий логин может держать вход жертвы закрытым до 15 мин за раз; смягчают IP-лимит и сброс счётчика успешным входом. Попытка по аккаунту тратится и на промежуточный вызов `Auth` до ввода кода 2FA. Лимит на `reset_id` тратят и верные коды, отвергнутые из-за «новый пароль = текущему». Лимит `ResetPassword` по аккаунту при отказе не раскрывает существование аккаунта (тот же вид ответа). Очистки просроченных `ConfirmationCodes`/`ResetPasswords`/`WebAuthnChallenges` по-прежнему нет. Клиенты Android/iOS/Mac новых кодов ошибок не знают — покажут общую ошибку.
