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

## Регистрация (с подтверждением кодом по почте)

В BarkCloud **есть** сервис уведомлений [[modules/backend-notification]] (паритет с BarkFluff), поэтому Web использует штатный клиентский email-флоу `IdentityApi` — тот же, что и мобильные клиенты. Двухшаговый процесс (`Auth/RegistrationGateway.cs`):

**Шаг 1 — `BeginAsync` (POST `/register`):**
1. `UsersServerApi.CheckExistUsername` / `CheckExistEmail` — проверка занятости.
2. `IdentityApi.CreateAccount(first/last/username/email)` с device-метаданными → Identity создаёт черновик, шлёт письмо `ConfirmationRegistration` с 6-значным кодом, возвращает `code_id`.
3. Рендер экрана ввода кода (`flash.kind=register_confirm`); `code_id` и пароль несутся в скрытых полях формы (как в 2FA-шаге).

**Шаг 2 — `ConfirmAsync` (POST `/register/confirm`):**
1. `IdentityApi.ConfirmAccount(code_id, code)` → `refresh_token` (Identity шлёт `SuccessfulRegistration`).
2. `IdentityApi.CreateToken(refresh)` → `access_token` (ConfirmAccount отдаёт только refresh).
3. `IdentityApi.SetPassword(password, old="")` с access-токеном. Для нового пользователя это первая установка пароля → письмо `PasswordChanged` **не** шлётся.
4. `AuthGateway.IssueSession` ставит cookie → редирект на `/photos`.

> Раньше регистрация шла в обход почты через серверные API (`AddDraftUser → ConfirmUser → ForceSetPasswordServer → CreateSessionForUserServer`) — это не спрашивало код и ошибочно слало письмо «Пароль изменён администратором» (`ForceSetPasswordServer`). Заменено на клиентский флоу выше; серверный `IdentityServerApi`-клиент в Web больше не используется.

## Восстановление пароля «Забыли пароль?» (код по почте)

Двухшаговый флоу через клиентский `IdentityApi` (`Auth/PasswordResetGateway.cs`); ссылки «Забыли?» на странице логина ведут на `/forgot`.

**Шаг 1 — `BeginAsync` (POST `/forgot`):** `IdentityApi.ResetPassword(email|username, OtpTypeId.Email)` с device-метаданными → письмо `ResetPassword` с кодом, `reset_id`. Identity анти-энумерационно отдаёт dummy `reset_id` даже для несуществующего пользователя → всегда показываем экран ввода кода (`flash.kind=forgot_confirm`).

**Шаг 2 — `ConfirmAsync` (POST `/forgot/confirm`):** `IdentityApi.ConfirmResetPassword(reset_id, code)` → access+refresh (Identity очищает старый хеш пароля) → `IdentityApi.SetPassword(newPassword, old="")` (старый пароль не нужен — хеш очищен) → `AuthGateway.IssueSession` → `/photos`. Новый пароль вводится на шаге 2; `reset_id` несётся в скрытом поле.

## Файлы

### Корень
- `Program.cs` — `LoadConfiguration(ServiceId.Web)`, DI gRPC-клиентов (Identity/Users/Files/Cloud/**Album** + серверные `UsersServerApi`/`FilesServerApi` с `JwtClientInterceptor` — для проверки занятости и аватара), `AddHttpClient("files-upload")` для прокси-загрузки, лимиты тела запроса 512 МБ (Kestrel + `FormOptions`), регистрация сервисов (+ `AuthGateway`, `RegistrationGateway`, `PasswordResetGateway`, `AdminGate`, `DockerService`), `MapWebEndpoints` + `MapCloudApiEndpoints` + `MapSystemEndpoints` + `MapSettingsEndpoints`, включение h2c.
- `WebEndpoints.cs` — маршруты страниц: `/`, `/login` (GET/POST), `/logout`, `/register` (GET/POST), `/register/confirm` (POST), `/forgot` (GET/POST), `/forgot/confirm` (POST), `/photos`, `/files`, `/trash`, `/favorites`, `/settings`, `/videos`, `/shared`, `/shared.jsx`, `/shared.css`. Для `/photos`/`/files`/`/trash`/`/favorites`/`/videos` `page_data_json` пустой — данные грузятся на клиенте через `/api`.
- `Endpoints/CloudApiEndpoints.cs` — группа `/api/*` для Фото/Видео/Файлов (см. раздел «Фото/Видео/Файлы»).
- `SystemEndpoints.cs` — `/healthz` + группа `/api/system/*` (обновление/перезапуск бэкенда). См. [[modules/web-system-updates]].
- `SettingsEndpoints.cs` — группа `/api/settings/*` для действий страницы настроек (см. раздел «Настройки»).

### Auth
- `AuthGateway.cs` — cookie, локальная валидация JWT, refresh, логин/логаут, `IssueSession` (общая выдача cookie сессии), `ClearSession` (удаление cookie без обращения в Identity — после удаления аккаунта).
- `AdminGate.cs` — гейт админ-действий по паролю `App:AdminPassword` (cookie `bark_admin`, HMAC на `JwtSettings:SecretKey`). См. [[modules/web-system-updates]].
- `RegistrationGateway.cs` — регистрация с кодом по почте через клиентский `IdentityApi` (`BeginAsync`/`ConfirmAsync`, см. раздел «Регистрация»).
- `PasswordResetGateway.cs` — восстановление пароля «Забыли пароль?» через клиентский `IdentityApi` (`BeginAsync`/`ConfirmAsync`, см. раздел «Восстановление пароля»).
- `WebUser.cs` — модель пользователя + `LoginOutcome`/`LoginResult` + `RegistrationOutcome`/`RegistrationResult` + `PasswordResetOutcome`/`PasswordResetResult`.

### Infrastructure
- `TemplateRenderer.cs` — рендер плейсхолдеров `{{ }}` / `{{{ }}}` / `| default("…")` с JS-экранированием (не задевает JSX `style={{…}}`).
- `DeviceInfo.cs`, `BrowserContext.cs` — построение device-метаданных из запроса браузера.
- `ServiceToken.cs` — генерация сервисного JWT (`TokenType=Service`) из общего `JwtSettings:SecretKey`.
- `DockerService.cs` — управление контейнерами бэкенда через `docker.sock` (pull/up/restart/start/stop, self-update веба через helper-контейнер). См. [[modules/web-system-updates]].

### Rendering
- `PageService.cs` — чтение и рендер файлов из `Pages/`.
- `PageDataBuilder.cs` — сбор серверных данных: каркас (`Users.GetUser` + `Files.GetUserStorageInfo`) и Settings (профиль + bio + email через `UsersServerApi.GetUserContacts`, флаги 2FA `ListOtpVerification`, приватность `GetPrivacySettings`, сессии с `deviceId`, storage). Фото/Видео/Файлы серверно больше не собираются — они грузятся на клиенте через `/api`.
- `CloudJson.cs` — единый маппинг gRPC-типов Files → JSON-карточки (`Media`/`Dir`/`Album`/`Entry`/`Trash`), общий для `/api`. `Media` отдаёт `previews[]` (128/512/1024 с URL) для `srcset`; `Trash` добавляет `deletedAt`/`purgeAt`.
- `Format.cs`, `FileKind.cs` — форматирование размеров/дат и классификация файлов.

### Pages
- `Login Page Full.html`, `Photos.html`, `Files.html`, `Trash.html`, `Favorites.html`, `Settings.html`, `Videos.html`, `Shared.html`, `shared.jsx`, `shared.css` — React+Babel страницы; сервер заполняет `{{ … }}` (каркас) и `{{{ page_data_json }}}` (только Settings; Фото/Видео/Файлы/Корзина/Избранное грузят данные сами через `fetch('/api/…')`). `Trash.html` — страница «Корзина»: список удалённых файлов с датами удаления/окончательной зачистки, действия «Восстановить»/«Удалить навсегда»/«Очистить корзину». `Favorites.html` — страница «Избранное»: сетка превью (как Фото; для документов — плитка-иконка типа файла), группировка по датам, клик по медиа → `Lightbox`, по документу → скачивание, снятие звезды на hover.
- `shared.jsx` экспортирует в `window` не только каркас (`AppShell`/`Sidebar`/…), но и data-слой и общий UI: `api`/`apiGet`/`apiPost`/`pickFiles`/`uploadFile`, `MediaThumb` (превью через `srcset`+`sizes` — браузер сам выбирает ширину под блок), `Lightbox` (оригинал через `GetTempDownloadUrl`), `Modal`, `useToast`, `EmptyState`, `Loading`, а также общие для Фото/Видео компоненты альбомов: `AlbumCard`/`AlbumFormModal`/`PickMediaModal`/`AlbumDetail` + хелперы `plural`/`dateLabel`/`groupByDate`.
- Навигация в `shared.jsx` ведёт на чистые роуты (`/photos`, `/files`, `/settings`, …), а не на `*.html`.
- Логин-страница — многорежимная по `flash.kind`: `register` (`RegisterCard`, POST `/register`), `register_confirm` (`RegisterConfirmCard`, ввод кода, POST `/register/confirm`), `forgot` (`ForgotCard`, POST `/forgot`), `forgot_confirm` (`ForgotConfirmCard`, код + новый пароль, POST `/forgot/confirm`). Шаги подтверждения переиспользуют компонент `OtpRow` и несут `code_id`/`reset_id`/пароль в скрытых полях.
- **Тема** (light/dark/auto): `shared.css` содержит тёмную палитру `:root[data-theme="dark"]`; каждая страница приложения (кроме логина) в `<head>` имеет синхронный bootstrap-скрипт, который до рендера читает `localStorage.bark_theme` и ставит `data-theme`. Выбор темы — вкладка «Внешний вид» в настройках (хранится локально в браузере, без бэкенда).

## Фото / Видео / Файлы (данные и /api)

Страницы `/photos`, `/videos`, `/files` — клиентские: при монтировании грузят данные через `fetch('/api/…')`,
все действия (загрузка, создание/редактирование альбомов и папок, навигация) тоже идут через `/api`.
Эндпоинты — `Endpoints/CloudApiEndpoints.cs` (группа `/api`), паттерн как у `/api/settings/*`: `AuthGateway.AuthenticateAsync`
→ gRPC с пользовательским токеном (`BrowserContext.UserToken`); доменные ошибки (`FailedPrecondition` + `x-error-code`) → `400 { error, code }`.

- Каталоги (CloudApi): `GET cloud/list?dir=`, `POST cloud/dir`/`dir/rename`/`dir/move`/`dir/delete`, `POST cloud/attach`, `POST cloud/entry/rename`/`entry/move`/`entry/delete` (удаление файла/папки = перемещение в корзину).
- Корзина (CloudApi): `GET cloud/trash?limit=&cursorAt=&cursorId=` (`ListTrash`), `POST cloud/trash/restore`/`trash/purge` (по `entryId`), `POST cloud/trash/empty`.
- Галерея (CloudApi): `GET cloud/media?kind=photo|video&limit=&cursorAt=&cursorId=` (`ListUserMedia`, cursor-пагинация). Карточка галереи (`CloudJson.MediaItem`) несёт `entryIds`/`entryNames`/`entriesCount` (поле `UserImageItem.entry_ids` в proto) — нужны контекстному меню для переименования/удаления элемента галереи через `entry/rename`·`entry/delete`.
- Избранное (CloudApi): `GET cloud/favorites?limit=&cursorAt=&cursorId=` (`ListFavorites`, карточки `CloudJson.Media`), `POST cloud/favorites/add`/`cloud/favorites/remove` (по `fileId`, идемпотентно). Питает страницу `/favorites`; добавление/снятие — для будущего контекстного меню (и кнопки-звезды на самой странице).
- Альбомы (AlbumApi): `GET albums`, `GET albums/items?album=&kind=`, `POST albums`/`albums/update`/`albums/delete`/`albums/items/add`/`albums/items/remove`.
- Свойства файла: `GET files/info?id=` → серверный `FilesServerApi.GetFileData` (авторизуется сервисным токеном через интерцептор; эндпоинт сам проверяет владение — id пользователя ∈ `uploaders`, иначе 403). Полная карточка `UploadFileInfo` (+ etag) для модалки «Свойства».
- Файлы (FilesApi): `POST files/upload` — **прокси-загрузка**: `GetUploadUrl` → стрим байтов `HttpClient`-ом на **внутренний HTTP1-эндпоинт** Files `FilesService:Http1Base` (= хост из `FilesService:Host` + порт `FILES_HTTP1PORT`, по умолч. `7026`) `/upload/{fileId}` — минуя nginx/TLS и `ExternalEndpoint:Host`; публичный `upload.Url` — fallback. Возвращает `fileId`. `GET files/download?ids=` — временные ссылки на оригинал(ы) через `GetTempDownloadUrl`.

UI: в сетке — превью (`MediaThumb`: `<img srcset sizes>` поверх MD3-иконки-плейсхолдера по типу медиа, проявляется по `onLoad`). Галереи `/photos`·`/videos` грузятся бесконечной прокруткой (`useInfiniteMedia` + `IntersectionObserver`, cursor-пагинация). При открытии — оригинал в `Lightbox`: листание (боковые кнопки/`←`/`→`), зум фото колесом + drag-pan, перемотка видео `←`/`→` на ±5 c.
Загрузка фото/видео на `/photos`·`/videos` кладётся в авто-папку **«Недавно загруженные»** (создаётся при отсутствии, `cloud/attach` к ней) — чтобы у медиа была запись каталога и работали корзина/переименование; документы на `/files` привязываются к текущей папке.
ПКМ по карточке (фото/видео/файл/папка) даёт контекстное меню (`ContextMenu`/`useMediaActions` в `shared.jsx`): копировать ссылку (временная), переименовать (`RenameModal`), удалить в корзину (`ConfirmModal`), свойства (`PropertiesModal` → `files/info`), для фото/видео — добавить/удалить из альбома (submenu; членство считается клиентски через `ListAlbumItems`). Аватар пользователя в `sb-user` берётся из `user.avatar_url` (`PageDataBuilder.BuildShellAsync` → `ProfilePicturePreview`), с фоллбэком на инициалы.
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
- Превью/оригиналы грузит **браузер** по абсолютным URL `{ExternalEndpoint:Host}/web/download/{id}` — ключ Files `ExternalEndpoint:Host` обязан быть `https://cloud.barkfluff.com:7025` (со схемой **https**), иначе на https-странице превью — mixed-content и блокируются браузером. Загрузка байтов от этого ключа уже **не** зависит (идёт на внутренний `cloud-files:7026`).
- Аплоад большого файла: nginx-vhost фронта веб-клиента (`cloud.barkfluff.com:443` → cloud-web) должен иметь `client_max_body_size 512m;` (по умолчанию 1 МБ → `413`). Этот vhost — вне репозитория (`nginx/cloud.barkfluff.conf` покрывает только gRPC-порты 7020/7021/7025). На стороне .NET лимиты уже сняты в `Program.cs` (Kestrel `MaxRequestBodySize` + `FormOptions.MultipartBodyLengthLimit` = 512 МБ).
- Шаги с кодом (2FA-логин, подтверждение регистрации, сброс пароля) переносят логин/пароль/`code_id`/`reset_id` в скрытых полях формы — упрощение MVP, стоит заменить на короткоживущий pending-токен/серверное состояние.
