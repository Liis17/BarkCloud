# План: режим «без почты» (email-less mode)

> Статус: план (не выполнено). Удалить после исполнения (как прочие планы Docs/).
> Решения пользователя: email при регистрации **обязателен, но без кода** (валидируем формат/занятость, код не шлём); адаптируем **только веб** (Android заморожен, на iOS регистрации нет); бэкенд-правки обратносовместимы на уровне proto.

## Цель

Если в БД Configuration **хоть одно** из 4 полей почты пусто
(`Email:Host`, `Email:Port`, `Email:SenderEmail`, `Email:SenderPassword`, все под `ServiceId.Notification`) —
сервер переходит в режим без почты:

1. Регистрация — **мгновенная**, без отправки/подтверждения кода (остальные данные валидируются как сейчас).
2. В вебе **нет** «Забыли пароль?» и сброса пароля.
3. **Не публиковать** задачи (`EmailNotification`) в очередь Notification — чтобы не копились.
4. В разделе «Обслуживание» показать, что Notification не используется и его можно остановить/убрать из docker-compose.

## Ключевой принцип распространения флага

`GetConfiguration(serviceId)` отдаёт записи `serviceId` **и** `ServiceId.Unknown`
(`ConfigurationStorage.cs:24`), а Email-поля засеяны только под `Notification` — значит другие сервисы их не видят.
Решение: Configuration **вычисляет** синтетический ключ `Features:EmailEnabled` (под `Unknown`) из текущих Email-значений
и подмешивает в ответ всем сервисам. Каждый сервис читает его штатно на старте
(`LoadConfiguration` → `IConfiguration["Features:EmailEnabled"]`). Хранить флаг **не нужно** — он всегда свежий на старте.

> Следствие: смена SMTP-настроек в БД требует **перезапуска** Identity и Web (как и любая другая конфигурация — модель «load-at-startup»). Перезапуск доступен из раздела «Обслуживание».
> Дефолт при отсутствии ключа — `true` (обратная совместимость, если Configuration ещё не пересобран). Configuration **нужно пересобрать**, чтобы ключ начал отдаваться.

---

## Фаза 0 — Configuration: вычисление и раздача флага

**Файлы:**
- `Backend/BarkCloud.Configuration/Infrastructure/ConfigurationStorage.cs` (+ интерфейс `IConfigurationStorage`)
- `Backend/BarkCloud.Configuration/Features/GetConfiguration/GetConfigurationCommandHandler.cs`

**Изменения:**
1. В `IConfigurationStorage`/`ConfigurationStorage` добавить
   `Task<bool> IsEmailConfiguredAsync()`: вернуть `true`, если **все 4** записи
   (`Section="Email"`, `Key ∈ {Host,Port,SenderEmail,SenderPassword}`, `ServiceId=Notification`)
   существуют и `Value` непустой (`!string.IsNullOrWhiteSpace`).
2. В `GetConfigurationCommandHandler.Handle` после сборки `filteredConfigurations`
   вычислить `emailEnabled = await storage.IsEmailConfiguredAsync()` и **добавить** в ответ
   синтетический `ConfigurationItem { Section="Features", Key="EmailEnabled", Value = emailEnabled ? "true":"false", ServiceId=(int)ServiceId.Unknown }`.
   Отдаётся **всем** сервисам.

**Проверка:** `dotnet build` Configuration; запустить, дернуть `GetConfiguration` для любого serviceId — в ответе есть `Features:EmailEnabled`. **Configuration пересобрать в проде** (новый ключ).

**Коммит:** «configuration: вычисляемый флаг Features:EmailEnabled».

---

## Фаза 1 — Shared: общий хелпер чтения флага

**Файл:** `Backend/BarkCloud.GrpcServer/` — новый extension, напр. `ConfigurationFeatureExtensions.cs`:
```csharp
public static bool EmailEnabled(this IConfiguration cfg)
    => cfg.GetValue("Features:EmailEnabled", true); // дефолт true для back-compat
```
Используют и Identity, и Web.

**Проверка:** билд GrpcServer. **Коммит:** «shared: IConfiguration.EmailEnabled()».

---

## Фаза 2 — Identity: глушим очередь + мгновенная регистрация + блок email-фич

### 2.1 Центральный guard очереди (закрывает «не копить задачи»)
**Файлы:** `Infrastructure/NotificationQueueSender.cs`, `Program.cs:64`
- Зарегистрировать singleton-флаг (напр. `record FeatureFlags(bool EmailEnabled)` из `builder.Configuration.EmailEnabled()`).
- В `NotificationQueueSender` инжектить флаг; в `SendNotification` при `!EmailEnabled` — `return` (debug-лог).
  Это глушит **все 12** точек публикации разом (см. реестр ниже) — в очередь ничего не уходит.

### 2.2 Мгновенная регистрация (обратносовместимо)
**Файлы:** `Shared/BarkCloud.Proto/identity_api.proto`, `Features/CreateAccount/CreateAccountCommandHandler.cs`
- proto: в `CreateAccountResponse` добавить `Token refresh_token = 2;` (опционально; `Token` уже есть в proto).
- В `CreateAccountCommandHandler`: после создания/override черновика —
  если `!EmailEnabled`: **не** генерировать код, **не** публиковать; вызвать `usersClient.ConfirmUserAsync(UserId)`,
  создать refresh-токен (инжектить `IRefreshTokensStorage`, как в `ConfirmAccountCommandHandler`),
  вернуть `CreateAccountResponse { RefreshToken = ... }` (без `code_id`).
  Иначе — текущий двухшаговый путь без изменений.
- Валидации email/username (`:32-35`) и device-заголовков (`:37-50`) остаются — email обязателен.

### 2.3 Блокировка фич, невозможных без почты (защитный бэкенд-слой)
**Файлы:** `Features/ResetPassword/ResetPasswordCommandHandler.cs`, `Features/EnableOtpVerification/EnableOtpVerificationCommandHandler.cs`, `Features/Auth/AuthCommandHandler.cs`
- Добавить типизированное исключение, напр. `EmailServiceDisabledException` в `Shared/BarkCloud.Shared.Exceptions/Identity/` (с x-error-code, как прочие).
- `ResetPassword`: при `!EmailEnabled` → бросить `EmailServiceDisabledException` (по почте код доставить нельзя).
- `EnableOtpVerification`: при `!EmailEnabled` **и** запросе типа **Email** → бросить исключение. **TOTP/Authenticator не трогаем.**
- `Auth`: **сознательно НЕ менялся.** Менять enforcement 2FA (фактически обход email-OTP) — чувствительное к безопасности решение; в свежем email-less деплое email-OTP включить нельзя, а пилёж задач в очередь уже закрыт центральным guard'ом. Пограничный случай (аккаунт с email-OTP, у которого почту отключили задним числом) остаётся залогиненным-заблокированным — это корректная защита, а не утечка.

**Проверка:** `dotnet build` Identity; юнит-тесты: (а) `EmailEnabled=true` — старый двухшаговый путь не изменился; (б) `EmailEnabled=false` — `CreateAccount` возвращает refresh, ничего не публикуется; (в) guard `NotificationQueueSender` не публикует при выключенной почте; (г) `ResetPassword`/`EnableOtp(Email)` бросают исключение.

**Коммит(ы):** «identity: guard очереди уведомлений», «identity: мгновенная регистрация без почты», «identity: блок reset/email-2FA без почты».

---

## Фаза 3 — Web (backend): флаг, одностадийная регистрация, отключение forgot

**Файлы:** `Program.cs`, `Auth/RegistrationGateway.cs`, `Auth/PasswordResetGateway.cs`, `WebEndpoints.cs`
- `Program.cs:28+`: после `LoadConfiguration` — `var emailEnabled = builder.Configuration.EmailEnabled();`,
  зарегистрировать singleton-флаг, инжектить в `RegistrationGateway`, `WebEndpoints`, `PageDataBuilder`.
- `RegistrationGateway`: вынести «хвост» `ConfirmAsync` (`CreateToken → SetPassword → IssueSession`) в приватный
  `CompleteAsync(http, refreshToken, password)`. В `BeginAsync` при `!emailEnabled`:
  после `CreateAccount` (вернёт `refresh_token`) сразу `CompleteAsync(...)` → `RegistrationOutcome.Success`.
  При `emailEnabled` — текущий `PendingConfirmation`.
- `WebEndpoints.cs`: `/register/confirm` при `!emailEnabled` не достигается (Begin вернёт Success).
  `/forgot` и `/forgot/confirm` при `!emailEnabled` → редирект на `/login` (ранний выход в хендлере).
- В `LoginVars/RegisterVars/ForgotVars` добавить `["email.enabled"] = emailEnabled ? "true":"false"`.

**Проверка:** `dotnet build` Web; ручной прогон: при пустом SMTP регистрация создаёт аккаунт без экрана кода; `/forgot` редиректит на `/login`.

**Коммит:** «web: одностадийная регистрация и отключение сброса пароля без почты».

---

## Фаза 4 — Web (UI): логин-страница и раздел «Обслуживание»

### 4.1 Серверная страница логина
**Файл:** `Backend/BarkCloud.Web/Pages/Login Page Full.html`
- Прокинутый плейсхолдер `email.enabled`: при `"false"`
  - скрыть ссылку «Забыли?» (~строка 572 в `LoginCard`);
  - в switch по `flash.kind` (~строки 827-838) не рендерить `register_confirm`/`forgot`/`forgot_confirm`.

### 4.2 Раздел «Обслуживание» (React-SPA)
**Файлы:** `Rendering/PageDataBuilder.cs`, `ClientApp/src/lib/types.ts`, `ClientApp/src/pages/SettingsPage.tsx`
- `PageDataBuilder.BuildSettingsJsonAsync` → в `system{}` добавить `emailEnabled` (читать `_config.EmailEnabled()`).
- `types.ts`: `SettingsState.system` += `emailEnabled: boolean`.
- `SettingsPage.tsx` (`SystemSection`): при `!emailEnabled` у сервиса `notification`
  показать пометку «Не используется (почта не настроена)» + примечание
  «Можно остановить или удалить из `docker-compose.yml`/`.env`». Кнопка **Stop** остаётся
  (`DockerService` умеет stop, но **не редактирует** compose — удаление из compose остаётся ручным шагом администратора).
- `DockerService.Managed` **не меняем** (оставляем notification управляемым).

**Проверка:** `npm run build` в `ClientApp`; собрать Web; глазами: «Забыли?» и forgot-экраны скрыты; в «Обслуживании» у Notification пометка.

**Коммит:** «web-ui: скрытие сброса пароля и пометка Notification в обслуживании».

---

## Фаза 5 — Инфраструктура и память проекта

**Файлы:** `Backend/docker-compose.yml`, `Backend/docker-compose-dev.yml`, Obsidian-vault.
- В обоих compose рядом с сервисом `notification` — комментарий: «Опционален — при пустых `Email:*` в Configuration сервис не нужен (Identity не публикует задачи). Можно остановить/удалить».
- Обновить заметки vault (правило CLAUDE.md): `modules/backend-notification`, `modules/backend-identity`,
  `modules/backend-web`, `modules/web-system-updates`, `api/configuration-api` — отметить режим без почты и флаг `Features:EmailEnabled`.

**Коммит:** «infra+docs: пометки по режиму без почты».

---

## Полный реестр точек публикации `EmailNotification` (Identity → очередь)

Все идут через `NotificationQueueSender.SendNotification` — глушатся одним guard'ом (Фаза 2.1):

| # | Тип | Хендлер (file:line) | Поведение без почты |
|---|-----|---------------------|---------------------|
| 1 | ConfirmationRegistration | `CreateAccount/...:111` | не отправляется (instant-путь) |
| 2 | SuccessfulRegistration | `ConfirmAccount/...:134` | тихо пропущено (instant минует ConfirmAccount) |
| 3 | ConfirmationAuth (email-OTP вход) | `Auth/...:143` | фактор не требуется |
| 4 | FailedLogin | `Auth/...:238` | тихо пропущено |
| 5 | SuccessfulLogin | `Auth/...:305` | тихо пропущено |
| 6 | ResetPassword | `ResetPassword/...:197` | заблокировано (исключение) |
| 7 | PasswordChanged | `SetPassword/...:120` | тихо пропущено |
| 8 | ConfirmationOtpEmail (вкл. email-2FA) | `EnableOtpVerification/...:171` | заблокировано (исключение) |
| 9 | TwoFactorMethodChanged | `DisableOtpVerification/...:146` | тихо пропущено |
| 10 | TwoFactorMethodChanged | `ConfirmOtpVerification/...:180` | тихо пропущено |
| 11 | PasswordChangedByAdmin | `ForceSetPasswordServer/...:64` | тихо пропущено (уже в try/catch) |
| 12 | SuccessfulLogin (серверная сессия) | `CreateSessionForUserServer/...:113` | тихо пропущено |

## Риски и ограничения

- **Мобильные клиенты** в режиме без почты не поддержат мгновенную регистрацию (вне scope; proto-правка обратносовместима, ничего не ломает).
- **Существующие аккаунты с email-OTP 2FA / ожиданием сброса** при переключении в режим без почты теряют эти возможности. В свежем email-less деплое неактуально.
- **Перезапуск** Identity/Web нужен после смены SMTP в БД (модель load-at-startup).
- `DockerService` **не редактирует** compose — «убрать из compose» остаётся ручным шагом (UI показывает подсказку + Stop).

## Порядок исполнения (с проверками)

```
0. Configuration: флаг        → проверка: GetConfiguration отдаёт Features:EmailEnabled
1. Shared: хелпер             → проверка: билд GrpcServer
2. Identity: guard+instant+блок → проверка: билд + юнит-тесты обеих веток
3. Web backend                → проверка: билд + ручной прогон регистрации/forgot
4. Web UI                     → проверка: npm build + визуально
5. Infra/docs                 → проверка: docker compose config валиден
```
Сборка затронутого стека перед каждым коммитом; коммит после каждой задачи (без push) — по рабочему процессу проекта.
