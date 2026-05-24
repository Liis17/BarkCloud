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

## Файлы

### Корень
- `Program.cs` — `LoadConfiguration(ServiceId.Web)`, DI gRPC-клиентов (Identity/Users/Files/Cloud), регистрация сервисов, включение h2c.
- `WebEndpoints.cs` — маршруты: `/`, `/login` (GET/POST), `/logout`, `/photos`, `/files`, `/settings`, `/videos`, `/shared`, `/shared.jsx`, `/shared.css`.

### Auth
- `AuthGateway.cs` — cookie, локальная валидация JWT, refresh, логин/логаут.
- `WebUser.cs` — модель пользователя + `LoginOutcome`/`LoginResult`.

### Infrastructure
- `TemplateRenderer.cs` — рендер плейсхолдеров `{{ }}` / `{{{ }}}` / `| default("…")` с JS-экранированием (не задевает JSX `style={{…}}`).
- `DeviceInfo.cs`, `BrowserContext.cs` — построение device-метаданных из запроса браузера.

### Rendering
- `PageService.cs` — чтение и рендер файлов из `Pages/`.
- `PageDataBuilder.cs` — сбор данных из микросервисов: каркас (`Users.GetUser` + `Files.GetUserStorageInfo`), Settings (otp/sessions/devices/storage), Files (`CloudApi.ListDirectoryDetailed`), Photos (`CloudApi.ListUserImages`).
- `Format.cs`, `FileKind.cs` — форматирование размеров/дат и классификация файлов.

### Pages
- `Login Page Full.html`, `Photos.html`, `Files.html`, `Settings.html`, `Videos.html`, `Shared.html`, `shared.jsx`, `shared.css` — React+Babel страницы; сервер заполняет `{{ … }}` и `{{{ page_data_json }}}`.

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
