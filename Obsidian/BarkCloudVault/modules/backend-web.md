# Backend — Web

Parent: [[index]] · See also: [[api/identity-api]] · [[api/users-api]] · [[api/files-api]] · [[modules/shared-identity]] · [[modules/shared-auth]]

## Назначение

Веб-клиент BarkCloud: HTTP-сервер, который отдаёт браузеру HTML-страницы из `Pages/` и выступает gRPC-**клиентом** к микросервисам по docker-сети. Не gRPC-сервер — браузер общается с ним по HTTP/1.1, а наружу к Identity/Users/Files уходят gRPC-вызовы (h2c).

Поток: нет валидного токена в cookie → страница логина; валидный токен → главная страница (Фото). Серверные данные подставляются в шаблонные плейсхолдеры страниц.

## Расположение

`Backend/BarkCloud.Web/`

## Аутентификация

- Cookie: `bark_at` (access), `bark_rt` (refresh), `bark_did` (стабильный device-id). HttpOnly, SameSite=Lax.
- Токен валидируется **локально** по `JwtSettings:SecretKey/Issuer/Audience`. Эти значения web получает из **Configuration-сервиса** через `LoadConfiguration(ServiceId.Web)` — `JwtSettings` засеяны как общие (`ServiceId.Unknown`) и раздаются любому сервису, поэтому секрет автоматически совпадает с Identity.
- Истёк access → автоматический refresh через `IdentityApi.CreateToken`.
- Логин → `IdentityApi.Auth` (с device-заголовками `x-device-name`/`x-os-name`/`x-app-name`/`x-app-version`, base64). Поддержан 2FA-шаг.

## Регистрация (без подтверждения по почте и 2FA)

В BarkCloud **нет** сервиса уведомлений (был только в BarkFluff), поэтому email-флоу `CreateAccount`/`ConfirmAccount` не используется. Аккаунт собирается целиком на стороне Web через **серверные (inter-service) API** и пользователь сразу логинится:

1. `UsersServerApi.CheckExistUsername` / `CheckExistEmail` — проверка занятости.
2. `UsersServerApi.AddDraftUser` (при остатке черновика → `OverrideDraftUser`) → `userId`.
3. `UsersServerApi.ConfirmUser` → пользователь подтверждён.
4. `IdentityServerApi.ForceSetPasswordServer` → пароль (без OTP).
5. `IdentityServerApi.CreateSessionForUserServer` → access+refresh, регистрирует устройство.
6. `AuthGateway.IssueSession` ставит cookie → редирект на `/photos`.

Серверные вызовы авторизуются **сервисным JWT** (`TokenType=Service`), который Web подписывает общим `JwtSettings:SecretKey` сам (`Infrastructure/ServiceToken.cs`) — это снимает зависимость от засева `*Service:Token` в Configuration (на существующей БД seed не перезапускается). Email-уведомления внутри серверных обработчиков обёрнуты в try/catch, поэтому отсутствие Notifications регистрацию не ломает.

> Цена подхода: Web держит привилегированный сервисный токен (может создавать сессии и менять пароли любому пользователю). Для self-host приемлемо.

## Файлы

### Корень
- `Program.cs` — `LoadConfiguration(ServiceId.Web)`, DI gRPC-клиентов (Identity/Users/Files/Cloud + серверные `UsersServerApi`/`IdentityServerApi` с `JwtClientInterceptor`), регистрация сервисов, включение h2c.
- `WebEndpoints.cs` — маршруты: `/`, `/login` (GET/POST), `/logout`, `/register` (GET/POST), `/photos`, `/files`, `/settings`, `/videos`, `/shared`, `/shared.jsx`, `/shared.css`.

### Auth
- `AuthGateway.cs` — cookie, локальная валидация JWT, refresh, логин/логаут, `IssueSession` (общая выдача cookie сессии).
- `RegistrationGateway.cs` — регистрация без почты через серверные API (см. раздел «Регистрация»).
- `WebUser.cs` — модель пользователя + `LoginOutcome`/`LoginResult` + `RegistrationOutcome`/`RegistrationResult`.

### Infrastructure
- `TemplateRenderer.cs` — рендер плейсхолдеров `{{ }}` / `{{{ }}}` / `| default("…")` с JS-экранированием (не задевает JSX `style={{…}}`).
- `DeviceInfo.cs`, `BrowserContext.cs` — построение device-метаданных из запроса браузера.
- `ServiceToken.cs` — генерация сервисного JWT (`TokenType=Service`) из общего `JwtSettings:SecretKey`.

### Rendering
- `PageService.cs` — чтение и рендер файлов из `Pages/`.
- `PageDataBuilder.cs` — сбор данных из микросервисов: каркас (`Users.GetUser` + `Files.GetUserStorageInfo`), Settings (otp/sessions/devices/storage), Files (`CloudApi.ListDirectoryDetailed`), Photos (`CloudApi.ListUserImages`).
- `Format.cs`, `FileKind.cs` — форматирование размеров/дат и классификация файлов.

### Pages
- `Login Page Full.html`, `Photos.html`, `Files.html`, `Settings.html`, `Videos.html`, `Shared.html`, `shared.jsx`, `shared.css` — React+Babel страницы; сервер заполняет `{{ … }}` и `{{{ page_data_json }}}`.
- Навигация в `shared.jsx` ведёт на чистые роуты (`/photos`, `/files`, `/settings`, …), а не на `*.html`.
- Логин-страница содержит экран регистрации как состояние `flash.kind = "register"` (компонент `RegisterCard`, POST `/register`).

## Конфигурация

Из **Configuration-сервиса** (`CONFIGURATION_SERVICE_URL`, как у остальных):
- `JwtSettings:*` — общие (ServiceId.Unknown).
- `IdentityService:Host` / `UsersService:Host` / `FilesService:Host` — адреса в docker-сети
  (`http://cloud-identity:7000`, `cloud-users:7001`, `cloud-files:7005`; засеяны для `ServiceId.Web`).
  Fallback на те же значения зашит в `Program.cs`, если Configuration их не отдал.

Из env / appsettings (UI-настройки, не секреты):
- `ASPNETCORE_URLS`, `App__PublicHost`, `App__CookieSecure`, `App__Version`.

> Для нового `ServiceId.Web` правились `Shared.Identity/ServiceId.cs` и `ConfigurationSeed.cs`
> (Configuration-сервис надо пересобрать; на существующей БД seed не перезапускается — сработает fallback в `Program.cs`).

## Инфраструктура

- `Dockerfile` / `Dockerfile.slim`, сервис `web` в `docker-compose.yml` и `docker-compose-dev.yml`.
- CI: `.github/workflows/build-backend-web.yml`.

## Ограничения / TODO

- Videos и Shared пока отдаются с demo-fallback (нет backing-данных по видео/шарам через клиентское API).
- 2FA-шаг переносит логин/пароль в скрытых полях формы — упрощение MVP, стоит заменить на короткоживущий pending-токен.
