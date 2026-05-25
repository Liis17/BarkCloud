# Backend — Web

Parent: [[index]] · See also: [[api/identity-api]] · [[api/users-api]] · [[api/files-api]] · [[modules/shared-identity]] · [[modules/shared-auth]]

Дочерние: [[modules/web-system-updates]] — раздел «Обслуживание» (обновление/перезапуск бэкенда из настроек).

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
- `Program.cs` — `LoadConfiguration(ServiceId.Web)`, DI gRPC-клиентов (Identity/Users/Files/Cloud/**Album** + серверные `UsersServerApi`/`IdentityServerApi`/`FilesServerApi` с `JwtClientInterceptor`), `AddHttpClient("files-upload")` для прокси-загрузки, лимиты тела запроса 512 МБ (Kestrel + `FormOptions`), регистрация сервисов (+ `AdminGate`, `DockerService`), `MapWebEndpoints` + `MapCloudApiEndpoints` + `MapSystemEndpoints` + `MapSettingsEndpoints`, включение h2c.
- `WebEndpoints.cs` — маршруты страниц: `/`, `/login` (GET/POST), `/logout`, `/register` (GET/POST), `/photos`, `/files`, `/settings`, `/videos`, `/shared`, `/shared.jsx`, `/shared.css`. Для `/photos`/`/files`/`/videos` `page_data_json` пустой — данные грузятся на клиенте через `/api`.
- `Endpoints/CloudApiEndpoints.cs` — группа `/api/*` для Фото/Видео/Файлов (см. раздел «Фото/Видео/Файлы»).
- `SystemEndpoints.cs` — `/healthz` + группа `/api/system/*` (обновление/перезапуск бэкенда). См. [[modules/web-system-updates]].
- `SettingsEndpoints.cs` — группа `/api/settings/*` для действий страницы настроек (см. раздел «Настройки»).

### Auth
- `AuthGateway.cs` — cookie, локальная валидация JWT, refresh, логин/логаут, `IssueSession` (общая выдача cookie сессии), `ClearSession` (удаление cookie без обращения в Identity — после удаления аккаунта).
- `AdminGate.cs` — гейт админ-действий по паролю `App:AdminPassword` (cookie `bark_admin`, HMAC на `JwtSettings:SecretKey`). См. [[modules/web-system-updates]].
- `RegistrationGateway.cs` — регистрация без почты через серверные API (см. раздел «Регистрация»).
- `WebUser.cs` — модель пользователя + `LoginOutcome`/`LoginResult` + `RegistrationOutcome`/`RegistrationResult`.

### Infrastructure
- `TemplateRenderer.cs` — рендер плейсхолдеров `{{ }}` / `{{{ }}}` / `| default("…")` с JS-экранированием (не задевает JSX `style={{…}}`).
- `DeviceInfo.cs`, `BrowserContext.cs` — построение device-метаданных из запроса браузера.
- `ServiceToken.cs` — генерация сервисного JWT (`TokenType=Service`) из общего `JwtSettings:SecretKey`.
- `DockerService.cs` — управление контейнерами бэкенда через `docker.sock` (pull/up/restart/start/stop, self-update веба через helper-контейнер). См. [[modules/web-system-updates]].

### Rendering
- `PageService.cs` — чтение и рендер файлов из `Pages/`.
- `PageDataBuilder.cs` — сбор серверных данных: каркас (`Users.GetUser` + `Files.GetUserStorageInfo`) и Settings (профиль + bio + email через `UsersServerApi.GetUserContacts`, флаги 2FA `ListOtpVerification`, приватность `GetPrivacySettings`, сессии с `deviceId`, storage). Фото/Видео/Файлы серверно больше не собираются — они грузятся на клиенте через `/api`.
- `CloudJson.cs` — единый маппинг gRPC-типов Files → JSON-карточки (`Media`/`Dir`/`Album`/`Entry`), общий для `/api`. `Media` отдаёт `previews[]` (128/512/1024 с URL) для `srcset`.
- `Format.cs`, `FileKind.cs` — форматирование размеров/дат и классификация файлов.

### Pages
- `Login Page Full.html`, `Photos.html`, `Files.html`, `Settings.html`, `Videos.html`, `Shared.html`, `shared.jsx`, `shared.css` — React+Babel страницы; сервер заполняет `{{ … }}` (каркас) и `{{{ page_data_json }}}` (только Settings; Фото/Видео/Файлы грузят данные сами через `fetch('/api/…')`).
- `shared.jsx` экспортирует в `window` не только каркас (`AppShell`/`Sidebar`/…), но и data-слой и общий UI: `api`/`apiGet`/`apiPost`/`pickFiles`/`uploadFile`, `MediaThumb` (превью через `srcset`+`sizes` — браузер сам выбирает ширину под блок), `Lightbox` (оригинал через `GetTempDownloadUrl`), `Modal`, `useToast`, `EmptyState`, `Loading`, а также общие для Фото/Видео компоненты альбомов: `AlbumCard`/`AlbumFormModal`/`PickMediaModal`/`AlbumDetail` + хелперы `plural`/`dateLabel`/`groupByDate`.
- Навигация в `shared.jsx` ведёт на чистые роуты (`/photos`, `/files`, `/settings`, …), а не на `*.html`.
- Логин-страница содержит экран регистрации как состояние `flash.kind = "register"` (компонент `RegisterCard`, POST `/register`).
- **Тема** (light/dark/auto): `shared.css` содержит тёмную палитру `:root[data-theme="dark"]`; каждая страница приложения (кроме логина) в `<head>` имеет синхронный bootstrap-скрипт, который до рендера читает `localStorage.bark_theme` и ставит `data-theme`. Выбор темы — вкладка «Внешний вид» в настройках (хранится локально в браузере, без бэкенда).

## Фото / Видео / Файлы (данные и /api)

Страницы `/photos`, `/videos`, `/files` — клиентские: при монтировании грузят данные через `fetch('/api/…')`,
все действия (загрузка, создание/редактирование альбомов и папок, навигация) тоже идут через `/api`.
Эндпоинты — `Endpoints/CloudApiEndpoints.cs` (группа `/api`), паттерн как у `/api/settings/*`: `AuthGateway.AuthenticateAsync`
→ gRPC с пользовательским токеном (`BrowserContext.UserToken`); доменные ошибки (`FailedPrecondition` + `x-error-code`) → `400 { error, code }`.

- Каталоги (CloudApi): `GET cloud/list?dir=`, `POST cloud/dir`/`dir/rename`/`dir/move`/`dir/delete`, `POST cloud/attach`, `POST cloud/entry/rename`/`entry/move`/`entry/delete`.
- Галерея (CloudApi): `GET cloud/media?kind=photo|video&limit=&cursorAt=&cursorId=` (`ListUserMedia`, cursor-пагинация).
- Альбомы (AlbumApi): `GET albums`, `GET albums/items?album=&kind=`, `POST albums`/`albums/update`/`albums/delete`/`albums/items/add`/`albums/items/remove`.
- Файлы (FilesApi): `POST files/upload` — **прокси-загрузка** (получает upload-URL через `GetUploadUrl`, стримит байты `HttpClient`-ом на публичный URL, возвращает `fileId`; без CORS на Files); `GET files/download?ids=` — временные ссылки на оригинал(ы) через `GetTempDownloadUrl`.

UI: в сетке — превью (`<img srcset sizes>`, размер под блок и DPR), при открытии — оригинал (Lightbox через `files/download`).
Загрузка фото идёт в галерею; на `/files` после загрузки файл привязывается к текущей папке (`cloud/attach`).
Альбомы общие для Фото и Видео (`ListAlbums`). Шаринг/EXIF/история активности в Files API отсутствуют — в инспекторе Files не показываются.

## Настройки (рабочие параметры)

Страница `/settings` — настоящие вкладки (левая навигация `.set-nav`, состояние в URL hash, рендерится только активная секция). Вкладки: Аккаунт, Безопасность, Приватность, Хранилище, Устройства и сессии, Внешний вид, Обслуживание (только если задан `App:AdminPassword`).

Действия идут через REST `/api/settings/*` (`SettingsEndpoints.cs`) — паттерн как у `/api/system/*`: проверка `AuthGateway.AuthenticateAsync`, затем gRPC с токеном пользователя (`BrowserContext.UserToken`), ошибки маппятся в `BadRequest { message }`. Эндпоинты:

- Профиль (UsersApi): `POST profile/name`, `POST profile/bio`, `GET profile/username-available?u=`, `POST profile/username`.
- Приватность (UsersApi): `GET/POST privacy` (`UpdatePrivacySettings` целиком; 0=Всем,1=Контактам,2=Никому).
- Безопасность (IdentityApi): `POST security/password` (`old_password` обязателен — пароль уже задан), `GET security/2fa`, `POST security/2fa/enable` (→ qr+code), `…/confirm`, `…/disable`.
- Устройства/сессии (Identity+Users): `GET sessions`, `POST devices/rename`, `POST sessions/revoke` (один `RemoveActiveSession` — он сам удаляет устройство в Users; текущую сессию завершать запрещено), `POST sessions/revoke-others`.
- Аккаунт: `POST account/delete` (`DeleteAccount` + `ClearSession` → клиент уходит на `/login`).
- Аватар: `POST avatar` (multipart `file` → `FilesServerApi.UploadAvatarServer` → `UsersServerApi.SetProfilePictureServer`), `POST avatar/remove` (тот же server-API с пустыми URL).

Удалены секции-плейсхолдеры без бэкенда (уведомления, язык, API-интеграции, E2E, резервные коды, экспорт/деактивация).

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
- Для раздела «Обслуживание» финальный образ `Dockerfile` переведён на `aspnet:10.0-alpine` + `docker-cli`/`docker-cli-compose`, а сервис `web` в `docker-compose.yml` получает `user: root` и монтирует `docker.sock` / compose / `.env` / `~/.docker/config.json`. Подробности и компромисс безопасности — [[modules/web-system-updates]].

## Ограничения / TODO

- `Shared` (Общие) пока отдаётся с demo-fallback — нет RPC шаринга в Files API.
- Длительность видео не приходит из Files API — в карточках видео показываются только разрешение (по `image_width/height`) и размер.
- Загрузка проксируется через веб-сервер на публичный upload-URL из `GetUploadUrl`; если этот URL недоступен из контейнера web, понадобится конфиг внутреннего HTTP-базиса Files (`cloud-files:7026`).
- 2FA-шаг переносит логин/пароль в скрытых полях формы — упрощение MVP, стоит заменить на короткоживущий pending-токен.
