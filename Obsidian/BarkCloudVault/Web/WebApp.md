# Web — приложение

Parent: [[Architecture]] · See also: [[Web/Settings]] · [[Web/SystemUpdates]] · [[Web/S3Migration]] · [[Web/TextViewer]] · [[Backend/ResumableUpload]] · [[Platform/Infrastructure]]

## Назначение

`Backend/BarkCloud.Web/` — HTTP-приложение на ASP.NET Core и gRPC-клиент микросервисов. Оно раздаёт React SPA, выполняет HTTP API для браузера и проксирует запросы в Configuration, Identity, Users, Files и Torrent. Web не поднимает gRPC-сервер.

Клиентские исходники находятся в `Backend/BarkCloud.Web/ClientApp/`; Vite собирает их в `wwwroot/`. Серверные страницы входа и регистрации обслуживаются отдельно от маршрутов SPA.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Web/Program.cs` | top-level host | Конфигурация, gRPC-клиенты, DI и HTTP-маршруты |
| `Backend/BarkCloud.Web/AppVersion.cs` | `AppVersion.Current` | Версия по умолчанию |
| `Backend/BarkCloud.Web/Auth/AuthGateway.cs` | `AuthGateway` | Сессия браузера, refresh и проверка отзыва |
| `Backend/BarkCloud.Web/WebEndpoints.cs` | `MapWebEndpoints` | Вход, регистрация, выход и публичные ссылки |
| `Backend/BarkCloud.Web/Endpoints/CloudApiEndpoints.cs` | `MapCloudApiEndpoints` | HTTP API облачных файлов и пользовательского интерфейса |
| `Backend/BarkCloud.Web/ClientApp/src/main.tsx` | React entry | Точка входа SPA |

## Публичные контракты

| Контракт | Поведение |
|---|---|
| `AppVersion.Current` | Единственный источник версии по умолчанию (поднимается при каждой функциональной правке веба); конфигурация `App:Version` может его переопределить. Версия попадает в данные оболочки и метаданные устройства клиента |
| Cookie `bark_at`, `bark_rt`, `bark_did` | Access-, refresh- и device-id cookie. Все HttpOnly, режим Secure задаётся `App:CookieSecure` |
| `/login`, `/register`, `/forgot` | Серверные страницы и формы входа, регистрации и сброса пароля |
| `POST /login/webauthn/begin`, `POST /login/webauthn/complete` | Начало и завершение входа WebAuthn |
| `GET /api/me`, `GET /api/storage` | Данные оболочки приложения и отдельная статистика хранилища |
| `/api/settings/*`, `/api/system/*` | API настроек и обслуживания; детали в [[Web/Settings]] и [[Web/SystemUpdates]] |
| `/s/{token}`, `/v/{token}`, `/m/{token}`, `/f/{token}`, `/al/{token}`, `/mpl/{token}` | Публичные ссылки на файлы, медиа, папки, альбомы и музыкальные плейлисты |

## Зависимости и взаимодействия

- При запуске Web получает адреса сервисов и общую конфигурацию через `LoadConfiguration(ServiceId.Web)`. Внутренние gRPC-соединения идут по HTTP/2 без TLS внутри Docker-сети.
- Пользовательский access-токен проверяется локально. При истечении или отзыве Web запрашивает токены по refresh-cookie у Identity. Локальный кэш отзыва синхронизируется с Identity; см. [[Backend/SessionRevocation]].
- Большинство `/api/*`-маршрутов требует пользовательскую сессию. Неизвестный `/api/*` возвращает 404; остальные маршруты SPA обслуживаются fallback-маршрутом и требуют сессию, кроме публичных страниц ссылок.
- HTTP-клиент `files-upload` проксирует скачивание и просмотр файлов из Files со сроком по умолчанию (100 с, действует до получения заголовков). Legacy `POST /api/files/upload` использует отдельный клиент `LegacyUploadTransfer.ClientName` (`files-legacy-upload`, `Infrastructure/LegacyUploadTransfer.cs`) со сроком 2 ч — дольше nginx (`proxy_read_timeout 7200s`) ответ браузеру не доставит; отмена браузера останавливает передачу в Files.
- Настройки, обслуживание, просмотр текста и поток загрузки описаны отдельно: [[Web/Settings]], [[Web/SystemUpdates]], [[Web/TextViewer]], [[Backend/ResumableUpload]].
