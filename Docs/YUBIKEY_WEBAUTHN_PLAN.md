# План: вход по YubiKey / FIDO2 (WebAuthn passwordless)

> Статус: план (не выполнено). Удалить после исполнения (как прочие планы Docs/).
>
> **Решения пользователя:**
> - Режим: **FIDO2/WebAuthn passwordless** (вход ключом, без пароля). Пароль и текущий `Auth` остаются для регистрации, привязки первого ключа и восстановления.
> - Идентификация: **username-first** (пользователь вводит логин → сервер отдаёт `allowCredentials` → касание). Resident key НЕ требуется.
> - Recovery: **fallback на существующий пароль + Email-OTP** (ничего нового не добавляем).
> - Развёртывание: **допускаются self-hosted** инстансы → RP ID = домен сервера (свой на инстанс), не зашит. Вход по ключу требует **доменный хост + валидный TLS** (голый IP не поддерживается — ограничение WebAuthn).
> - Drive: **только вход** (`GetAssertion`). Привязка/удаление ключей — только в Web.

## Цель

Дать вход по аппаратному ключу (YubiKey и любой FIDO2-authenticator, включая Windows Hello) **без пароля**, переиспользуя один серверный механизм на всех клиентах. Бэкенд (`Identity`) — единственный держатель credential'ов и единственное место валидации (через `Fido2NetLib`). Web и Drive — **тонкие релеи**: проксируют challenge/assertion между WebAuthn-клиентом (браузер / `webauthn.dll`) и `Identity`.

## Архитектура (один бек на всех)

```
                 ┌───────────────── Identity (Fido2NetLib) ─────────────────┐
                 │  WebAuthnCredential, WebAuthnChallenge, UserHandle        │
                 │  Begin/Complete Registration · Begin/Complete Assertion   │
                 │  List/Remove · выдача access+refresh (как Auth)           │
                 └───▲───────────────────────▲───────────────────────▲──────┘
        gRPC IdentityApi │                    │ gRPC                  │ gRPC
            ┌────────────┴───────┐   ┌─────────┴─────────┐   ┌─────────┴─────────┐
            │  Web (релей)       │   │  Drive.Engine     │   │ Android/iOS (позже)│
            │  navigator.creds   │   │  (релей)          │   │                   │
            └──────▲─────────────┘   └───▲───────────────┘   └───────────────────┘
                   │ браузер               │ IPC (JsonRpc)
              PublicKeyCredential     Drive.App → webauthn.dll (GetAssertion, HWND)
```

**Принцип передачи WebAuthn-данных через proto:** options и ответы authenticator'а передаются как **JSON-строки** (`Fido2NetLib` сериализует/десериализует их штатно, браузер отдаёт JSON-сериализуемые объекты). Не дублируем сложные вложенные структуры WebAuthn в `.proto`.

**Выдача токенов:** успешная assertion завершается тем же «хвостом», что и `AuthCommandHandler` (`AuthCommandHandler.cs:251-326`): удалить старые refresh по deviceId, создать refresh, `CreateTokenCommand` → access, `RegisterDevice` в Users, уведомление `SuccessfulLogin`, метрики. Этот хвост выносится в общий сервис (см. Фаза 3.0).

> Перед реализацией каждой фазы с библиотекой — `use context7` по `Fido2NetLib` (серверные API) и по Windows WebAuthn API (`webauthn.dll`): сигнатуры/версии под .NET 10 могли измениться. Проверить совместимость `Fido2NetLib` с net10.0 через NuGet.

---

## Фаза 1 — Proto: контракты WebAuthn

**Файл:** `Shared/BarkCloud.Proto/identity_api.proto`

В `service IdentityApi` добавить 6 методов:
```proto
// Привязка ключа (требует авторизации)
rpc BeginWebAuthnRegistration(BeginWebAuthnRegistrationRequest) returns(BeginWebAuthnRegistrationResponse);
rpc CompleteWebAuthnRegistration(CompleteWebAuthnRegistrationRequest) returns(CompleteWebAuthnRegistrationResponse);
// Вход ключом (публичные, без токена — как Auth)
rpc BeginWebAuthnAssertion(BeginWebAuthnAssertionRequest) returns(BeginWebAuthnAssertionResponse);
rpc CompleteWebAuthnAssertion(CompleteWebAuthnAssertionRequest) returns(AuthResponse);
// Управление ключами (требует авторизации)
rpc ListWebAuthnCredentials(ListWebAuthnCredentialsRequest) returns(ListWebAuthnCredentialsResponse);
rpc RemoveWebAuthnCredential(RemoveWebAuthnCredentialRequest) returns(RemoveWebAuthnCredentialResponse);
```
Сообщения:
```proto
message BeginWebAuthnRegistrationRequest { }                       // userId из токена
message BeginWebAuthnRegistrationResponse { string options_json = 1; string challenge_id = 2; }
message CompleteWebAuthnRegistrationRequest { string challenge_id = 1; string attestation_json = 2; string credential_name = 3; }
message CompleteWebAuthnRegistrationResponse { }

message BeginWebAuthnAssertionRequest { oneof login { string username = 1; string email = 2; } }
message BeginWebAuthnAssertionResponse { string options_json = 1; string challenge_id = 2; }
message CompleteWebAuthnAssertionRequest { string challenge_id = 1; string assertion_json = 2; }
// ответ = AuthResponse (access+refresh)

message ListWebAuthnCredentialsRequest { }
message ListWebAuthnCredentialsResponse {
  message Credential { string id = 1; string name = 2; google.protobuf.Timestamp created_at = 3; google.protobuf.Timestamp last_used_at = 4; }
  repeated Credential credentials = 1;
}
message RemoveWebAuthnCredentialRequest { string credential_id = 1; }
message RemoveWebAuthnCredentialResponse { }
```

**Проверка:** билд решения — proto перегенерируется автоматически (Grpc.Tools) в Identity (Server), Web (Client), Drive.Engine (Client).
**Коммит:** «proto: контракты WebAuthn (registration/assertion/list/remove)».

> Swift/Kotlin `*.pb.swift`/Java не регенерируем — Android/iOS вне scope (см. [[swift-proto-regen]]).

---

## Фаза 2 — Identity: домен, хранилище, конфиг Fido2

### 2.1 Сущности
**Файлы:** `Backend/BarkCloud.Identity/Domain/WebAuthnCredential.cs`, `WebAuthnChallenge.cs`; поле в `Domain/AuthUserProperty.cs`
- `WebAuthnCredential`: `Id (long, PK)`, `UserId (long)`, `CredentialId (byte[])`, `PublicKey (byte[])`, `SignatureCounter (long)`, `AaGuid (Guid)`, `CredType (string)`, `Name (string)`, `CreatedAt`, `LastUsedAt`.
- `WebAuthnChallenge`: `Id (Guid, PK)`, `UserId (long)`, `Type (enum reg/assert)`, `OptionsJson (string)`, `ExpiresAt (DateTime)`. TTL ~5 мин.
- `AuthUserProperty` += `byte[]? WebAuthnUserHandle` — случайный непубличный user handle на пользователя (один на всех его ключей), генерится при первой привязке.

### 2.2 DbContext + миграция
**Файл:** `Persistence/Contexts/IdentityContext.cs`
- `DbSet<WebAuthnCredential>`, `DbSet<WebAuthnChallenge>`; в `OnModelCreating` — уникальный индекс на `WebAuthnCredential.CredentialId`, индекс по `UserId`.
- `dotnet ef migrations add AddWebAuthn` (применяется автоматически на старте — `Program.cs:101 ctx.Database.Migrate()`).

### 2.3 Storage
**Файлы:** `Persistence/Services/IWebAuthnStorage.cs` + `WebAuthnStorage.cs` (по образцу `AuthPropertiesStorage`)
- credential'ы: `AddCredential`, `GetCredentialsByUserId`, `GetCredentialById(byte[])`, `UpdateCounter`, `RemoveCredential(userId, id)`, `GetUserHandle/SetUserHandle`.
- challenge'и: `SaveChallenge`, `GetChallenge(Guid)`, `DeleteChallenge`, `DeleteExpired`.
- Регистрация в `Program.cs:61-69`: `AddTransient<IWebAuthnStorage, WebAuthnStorage>()`.

### 2.4 Конфиг Fido2 (RP ID для self-hosted)
**Файлы:** `Program.cs`, `Settings/WebAuthnSettings.cs`, конфиг Configuration-сервиса
- Новый `WebAuthnSettings { RpId, ServerName, Origins[] }` под `ServiceId.Identity` (засеять в Configuration, как `JwtSettings`). `RpId` = публичный домен инстанса; `Origins` = `["https://"+RpId]` (плюс origin Drive, см. ниже).
- `Program.cs`: зарегистрировать `Fido2` singleton:
  ```csharp
  builder.Services.AddSingleton(new Fido2(new Fido2Configuration {
      ServerDomain = cfg.RpId, ServerName = cfg.ServerName, Origins = new HashSet<string>(cfg.Origins) }));
  ```
- Origin Drive: `webauthn.dll` формирует origin вида `https://<RpId>` → совпадает с Web. Один RP ID на инстанс ⇒ ключ, привязанный в Web, работает в Drive того же сервера.

**Проверка:** билд Identity; миграция применяется; сервис стартует с засеянным `WebAuthn:*`.
**Коммит:** «identity: сущности и хранилище WebAuthn + конфиг Fido2».

---

## Фаза 3 — Identity: фичи (CQRS) + gRPC

### 3.0 Общий выпуск сессии (рефакторинг-вынос)
**Файл:** `Services/SessionIssuer.cs` (новый)
- Вынести «хвост выдачи токенов» (`AuthCommandHandler.cs:251-326`) в `SessionIssuer.IssueAsync(userId, deviceId, requestContext)` → возвращает `AuthResponse` (refresh+access, RegisterDevice, SuccessfulLogin-уведомление, метрики).
- Использовать в новом `CompleteWebAuthnAssertion`. **`AuthCommandHandler` пока не трогаем** (не рефакторим рабочее без нужды; опциональный последующий шаг — переключить и его).

### 3.1–3.6 Фичи (по образцу `EnableOtpVerification`/`ConfirmOtpVerification`)
**Папки:** `Features/BeginWebAuthnRegistration/`, `CompleteWebAuthnRegistration/`, `BeginWebAuthnAssertion/`, `CompleteWebAuthnAssertion/`, `ListWebAuthnCredentials/`, `RemoveWebAuthnCredential/` (Command + Handler).

| Фича | Auth | Логика |
|---|---|---|
| BeginRegistration | `UserContext.UserId` | `fido2.RequestNewCredential(user{id=userHandle,name=username,displayName}, excludeCredentials=existing, residentKey=Discouraged, uv=Preferred)`; сохранить `OptionsJson` в challenge (TTL); вернуть `options_json`+`challenge_id`. UserHandle сгенерить, если нет. |
| CompleteRegistration | `UserContext.UserId` | загрузить challenge (TTL + принадлежность userId); `fido2.MakeNewCredentialAsync(attestation, options, IsCredentialIdUniqueToUser)`; сохранить `WebAuthnCredential`; удалить challenge; (опц.) уведомление «привязан ключ». |
| BeginAssertion | **публичный** | найти юзера по login (`UsersServerApi.FindByLogin`, как Auth); если нет ключей → унифицированное исключение (анти-enumeration, см. риски); `fido2.GetAssertionOptions(allowCredentials=credIds, uv)`; сохранить challenge; вернуть. |
| CompleteAssertion | **публичный** | загрузить challenge; найти credential по credId из ответа; `fido2.MakeAssertionAsync(assertion, options, publicKey, storedCounter, IsUserHandleOwner)`; обновить counter + `LastUsedAt`; `SessionIssuer.IssueAsync` → `AuthResponse`. |
| List | `UserContext.UserId` | вернуть ключи юзера (id, name, createdAt, lastUsedAt). |
| Remove | `UserContext.UserId` | удалить ключ по id с проверкой `UserId`. |

### 3.7 gRPC-сервис
**Файл:** `Host/IdentityApiService.cs` — 6 override-методов. `[Authorize(Policy = nameof(TokenType.User))]` на Registration/List/Remove. Assertion-методы — **без** `[Authorize]` (публичные, как `Auth`). Тело — `_mediator.Send(command)` (как существующие).

### 3.8 Исключения
**Файлы:** `Shared/BarkCloud.Shared.Exceptions/Identity/` — `NoWebAuthnCredentialsException`, `WebAuthnChallengeExpiredException`, `WebAuthnVerificationFailedException` (наследники `BaseGrpcException`, каждый со своим `ErrorCode` GUID + `ErrorMessage`). Web/Drive ловят по `x-error-code` (механизм уже есть).

**Проверка:** билд Identity; юнит-тесты: (а) полный цикл register→assertion выдаёт токены; (б) истёкший challenge → исключение; (в) подделанная подпись → verification failed; (г) Remove чужого ключа запрещён.
**Коммит(ы):** «identity: SessionIssuer», «identity: фичи регистрации ключа», «identity: фичи входа ключом», «identity: список/удаление ключей».

---

## Фаза 4 — Web (backend-релей)

**Файлы:** `Auth/AuthGateway.cs`, `WebEndpoints.cs`, `SettingsEndpoints.cs`

### 4.1 Вход (публичный)
- `AuthGateway`: `BeginWebAuthnAsync(http, login)` → `identity.BeginWebAuthnAssertionAsync(req, BuildDeviceInfo(...).ToMetadata())` → `{optionsJson, challengeId}`. `CompleteWebAuthnAsync(http, challengeId, assertionJson, remember)` → `identity.CompleteWebAuthnAssertionAsync(...)` → `SetCookie(bark_at/bark_rt)` (как `LoginAsync`, `AuthGateway.cs:236-246`). Маппинг `RpcException` по `x-error-code` (расширить существующий `switch`).
- `WebEndpoints`: `POST /login/webauthn/begin` (`{login}` → `{optionsJson, challengeId}`), `POST /login/webauthn/complete` (`{challengeId, assertionJson, remember}` → 200+куки / ошибка). Публичные, зарегистрировать **до** `MapFallback`.

### 4.2 Привязка/управление (в группе `/api/settings`, через `Do()`)
- `GET /security/webauthn` → `ListWebAuthnCredentialsAsync` → список.
- `POST /security/webauthn/register/begin` → `BeginWebAuthnRegistrationAsync` → `{optionsJson, challengeId}`.
- `POST /security/webauthn/register/complete` (`{challengeId, attestationJson, name}`) → `CompleteWebAuthnRegistrationAsync` → `{ok}`.
- `POST /security/webauthn/remove` (`{credentialId}`) → `RemoveWebAuthnCredentialAsync` → `{ok}`.
- Авторизованные — через `BrowserContext.UserToken(user.AccessToken)`; входные — через device-metadata без токена.

**Проверка:** билд Web; ручной прогон обоих потоков через curl/Postman (JSON in/out).
**Коммит:** «web: релей-эндпоинты входа и привязки WebAuthn».

---

## Фаза 5 — Web (UI)

### 5.1 Страница логина (серверный HTML)
**Файл:** `Pages/Login Page Full.html`
- В `LoginCard` (рядом с SSO-блоком `:606-614`) — кнопка «Войти ключом безопасности». Скрывать, если `!window.PublicKeyCredential`.
- JS-поток (vanilla): взять `login` из поля → `fetch POST /login/webauthn/begin` → `optionsJson` → `navigator.credentials.get()` → `fetch POST /login/webauthn/complete` → при `ok` redirect `/photos`. Пароль/2FA-fallback остаются.
- Конвертация base64url↔ArrayBuffer: подключить `@github/webauthn-json` через CDN (как `react` уже через unpkg, `:10-12`).

### 5.2 Настройки (React-SPA)
**Файлы:** `ClientApp/src/pages/SettingsPage.tsx`, `ClientApp/src/lib/types.ts`, `ClientApp/package.json`
- Новая карточка «Ключи безопасности» в `SecurityTab` между «Пароль» и «Двухфакторная» (`:660-844`).
- Список ключей (`apiGet /api/settings/security/webauthn`), «Добавить ключ» → `register/begin` → `navigator.credentials.create()` → `register/complete`; удаление → `remove`.
- `package.json`: добавить `@github/webauthn-json` (или `@simplewebauthn/browser`) — Vite-зависимость.
- `types.ts`: тип для списка ключей.

**Проверка:** `npm run build` в ClientApp + билд Web; визуально: вход ключом и привязка ключа в настройках на доменном https-стенде.
**Коммит:** «web-ui: вход ключом на логине + управление ключами в настройках».

---

## Фаза 6 — Drive (только вход)

### 6.1 IPC-контракт
**Файл:** `Drive/BarkCloud.Drive.Contracts/IDriveEngine.cs` (+ DTO `WebAuthnChallengeDto`)
- `Task<WebAuthnChallengeDto> BeginWebAuthnAsync(string login)` → `{OptionsJson, ChallengeId, RpId}` (RpId парсится из optionsJson).
- `Task<EngineStatus> CompleteWebAuthnAsync(string challengeId, string assertionJson)`.

### 6.2 Engine
**Файлы:** `Drive/BarkCloud.Drive.Engine/` фасад `IDriveEngine`, `TokenManager.cs`
- `BeginWebAuthnAsync` → `connection.Identity.BeginWebAuthnAssertionAsync`. `CompleteWebAuthnAsync` → `connection.Identity.CompleteWebAuthnAssertionAsync` → передать `AuthResponse` в `TokenManager`.
- `TokenManager`: извлечь приватный `ApplyTokens(AuthResponse)` из `LoginAsync` (хирургично) и переиспользовать; сохранить refresh через `TokenStore` (DPAPI), `StartRefreshLoop`.

### 6.3 App — webauthn.dll + UI
**Файлы:** `Drive/BarkCloud.Drive.App/WebAuthnClient.cs` (новый, P/Invoke), `FirstRunWizard.xaml(.cs)`
- `WebAuthnClient`: P/Invoke `webauthn.dll` (`WebAuthNGetApiVersionNumber`, `WebAuthNAuthenticatorGetAssertion`, `WebAuthNFreeAssertion`, `WebAuthNGetErrorName`). Маршалинг `WEBAUTHN_CLIENT_DATA` / `..._GET_ASSERTION_OPTIONS` / `WEBAUTHN_ASSERTION`. Формирует `clientDataJSON` (`type="webauthn.get"`, challenge из optionsJson, `origin="https://"+RpId`); возвращает `assertion_json` в формате `AuthenticatorAssertionRawResponse` (тот, что ждёт `Fido2NetLib`). HWND = `new WindowInteropHelper(Window.GetWindow(this)).Handle`.
- `FirstRunWizard.xaml` StepLogin: кнопка «Войти ключом безопасности». Обработчик: `login → _engine.BeginWebAuthnAsync → WebAuthnClient.GetAssertion(hwnd,…) → _engine.CompleteWebAuthnAsync → status.Authenticated`.
- **Ограничение IP/домена:** если адрес сервера в мастере — IP или не-https, кнопку скрыть/выключить (WebAuthn требует домен+TLS). Подсказка: «Вход по ключу доступен только для серверов с доменным именем и TLS».
- Доступность: проверять версию API (`webauthn.dll`, Win10 1903+); иначе скрыть.

**Проверка:** билд `Drive` (App+Engine); прогон входа ключом на доменном https-стенде; проверка fallback на пароль при IP-сервере.
**Коммит:** «drive: вход ключом безопасности (WebAuthn GetAssertion)».

---

## Фаза 7 — Конфигурация, инфраструктура

**Файлы:** Configuration seed, `Tools/BarkCloud.Builder` (если генерит env), `Backend/nginx/*`
- Засеять `WebAuthn:RpId`, `WebAuthn:ServerName`, `WebAuthn:Origins` под `ServiceId.Identity`. В `BarkCloud.Builder` добавить поля, если он генерит `.env`/compose для Identity.
- nginx: новые методы того же `IdentityApi` — маршрутизация **без изменений**.
- gRPC-Web CORS Identity (`Program.cs:47-53`) уже отдаёт `x-error-code` — менять не нужно (Web ходит серверным релеем, не из браузера напрямую).

**Проверка:** `docker compose config` валиден; Identity видит `WebAuthn:*` на старте.
**Коммит:** «infra: конфигурация WebAuthn (RP ID / origins)».

---

## Фаза 8 — Obsidian vault + память

**Файлы:** `Obsidian/BarkCloudVault/` — `modules/backend-identity`, `modules/backend-web`, `modules/windows-drive`, `api/identity-api` (правило CLAUDE.md): отметить новые методы, сущности, поток passwordless, ограничение домен+TLS.
**Коммит:** «docs: заметки vault по WebAuthn».

---

## Риски и ограничения

- **Домен + TLS обязательны.** WebAuthn привязан к домену (RP ID) и проверяет origin. Голый IP не поддерживается; для Drive self-signed TLS допустим (origin валидируется строкой, не сертификатом — у Drive уже `acceptAnyCert`), но **доменный хост обязателен**. UI Drive прячет кнопку для IP-серверов.
- **SignatureCounter.** Многие ключи/passkey всегда отдают `0` — не требовать строгого инкремента; аномалии логировать, не блокировать (политика в `MakeAssertionAsync`).
- **User enumeration** на `BeginWebAuthnAssertion`: отсутствие юзера/ключей раскрывает существование аккаунта. MVP — унифицированное исключение; усиление (фейковые `allowCredentials`) — опционально.
- **Passwordless ≠ нет пароля.** Пароль остаётся для регистрации/привязки первого ключа/восстановления (по решению — fallback пароль+Email-OTP).
- **Android/iOS** — вне scope; 4 метода и сущности переиспользуемы. Регенерация Swift/Kotlin proto — отдельно ([[swift-proto-regen]]).
- **Fido2NetLib под .NET 10** — проверить версию/совместимость (NuGet + Context7) на Фазе 2.
- **Windows WebAuthn API** — самый хрупкий кусок (маршалинг структур); сверить сигнатуры через Context7, рассмотреть проверенную managed-обёртку вместо ручного P/Invoke.

## Порядок исполнения (с проверками)

```
1. Proto                       → проверка: билд решения, классы перегенерированы
2. Identity: домен+storage+Fido2 → проверка: билд + миграция применяется
3. Identity: фичи+gRPC+исключения → проверка: билд + юнит-тесты цикла register→assertion
4. Web backend (релей)         → проверка: билд + curl обоих потоков
5. Web UI (login + settings)   → проверка: npm build + визуально на https-домене
6. Drive (только вход)         → проверка: билд + вход ключом на https-домене
7. Конфиг/инфра                → проверка: compose config + старт Identity с WebAuthn:*
8. Vault                       → проверка: заметки обновлены
```
Сборка затронутого стека перед каждым коммитом; коммит после каждой задачи (без push) — по рабочему процессу проекта ([[plan-workflow]]).
