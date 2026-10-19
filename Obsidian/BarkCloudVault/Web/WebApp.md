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
| `Backend/BarkCloud.Web/Auth/AuthEndpoints.cs` | `MapAuthEndpoints` | Серверные auth-маршруты, antiforgery и переходы входа, регистрации и сброса пароля |
| `Backend/BarkCloud.Web/Auth/AuthFlowStore.cs` | `AuthFlowStore` | Ограниченное process-local состояние auth-потоков, привязанное к device cookie |
| `Backend/BarkCloud.Web/Auth/AuthPageState.cs` | `AuthPageState` | Публичный JSON-контракт страницы входа React |
| `Backend/BarkCloud.Web/Rendering/AuthPageService.cs` | `AuthPageService` | HTML-оболочка, manifest-ресурсы Vite и безопасная вставка auth state |
| `Backend/BarkCloud.Web/WebEndpoints.cs` | `MapWebEndpoints` | Корень, публичные ссылки и image proxy |
| `Backend/BarkCloud.Web/Endpoints/CloudApiEndpoints.cs` | `MapCloudApiEndpoints` | HTTP API облачных файлов и пользовательского интерфейса |
| `Backend/BarkCloud.Web/ClientApp/src/main.tsx` | React entry | Точка входа SPA |
| `Backend/BarkCloud.Web/ClientApp/src/auth/main.tsx` | Auth React entry | Точка входа интерфейса входа и восстановления |

## Публичные контракты

| Контракт | Поведение |
|---|---|
| `AppVersion.Current` | Единственный источник версии по умолчанию (сейчас `v1.14.1`, поднимается при каждой функциональной правке веба); конфигурация `App:Version` может его переопределить. Версия попадает в данные оболочки и метаданные устройства клиента |
| Cookie `bark_at`, `bark_rt`, `bark_did` | Access-, refresh- и device-id cookie. Все HttpOnly, режим Secure задаётся `App:CookieSecure` |
| `GET /login`, `GET /register`, `GET /forgot` | Отдают лёгкую HTML-оболочку и state для React auth entry. `Accept: application/json` возвращает сам state; успешная операция возвращает `{ "redirectTo": "/photos" }` |
| `POST /login`, `/register`, `/register/confirm`, `/forgot`, `/forgot/confirm` | Формы входа, регистрации и сброса пароля; все POST требуют antiforgery token и отвечают с `no-store` |
| `POST /login/resend`, `/register/resend`, `/forgot/resend` | Повторный запрос email-кода в исходном потоке; resend не продлевает 15-минутный срок потока |
| `POST /login/webauthn/begin`, `POST /login/webauthn/complete` | WebAuthn; CSRF передаётся заголовком `X-CSRF-TOKEN`, assertion принимается как JSON object |
| `GET /logout`, `POST /logout` | Выход; GET сохранён для совместимости, оба маршрута находятся в auth-группе с `no-store` |
| `GET /api/me`, `GET /api/storage` | Данные оболочки приложения и отдельная статистика хранилища |
| `/api/settings/*`, `/api/system/*` | API настроек и обслуживания; детали в [[Web/Settings]] и [[Web/SystemUpdates]] |
| `/s/{token}`, `/v/{token}`, `/m/{token}`, `/f/{token}`, `/al/{token}`, `/mpl/{token}` | Публичные ссылки на файлы, медиа, папки, альбомы и музыкальные плейлисты |

## Зависимости и взаимодействия

- Отдельный React auth entry (`ClientApp/src/auth/main.tsx`) собирается Vite вместе с SPA. В production `AuthPageService` рекурсивно собирает CSS и modulepreload из manifest; dev server разрешён только через явный loopback `Auth:ViteDevServerUrl` в Development. Auth state вставляется в отдельный `application/json` script после подготовки оболочки и не проходит через общий `TemplateRenderer`.
- Auth state содержит текущий экран, безопасные значения полей и ошибки; пароли, OTP и backend code/reset IDs остаются вне ответа. POST auth-маршруты и WebAuthn требуют antiforgery, ответы auth-страниц и ошибок не кешируются.
- `AuthFlowStore` ограничен 1024 потоками и абсолютным TTL 15 минут. Поток привязан к HttpOnly `bark_did`, а per-flow lock сериализует подтверждение и resend. Секреты и Identity IDs хранятся на сервере; после рестарта процесса flow теряется. Регистрация использует атомарные `CreateAccountWithPassword` и `ConfirmAccountWithPassword`; экран `session-pending` повторяет только `CreateToken`.
- Auth shell использует локальный Roboto Flex из Vite output, системный моноширинный шрифт для кода и устанавливает `bark_theme` до CSS.
- При запуске Web получает адреса сервисов и общую конфигурацию через `await LoadConfigurationAsync(ServiceId.Web)`. Общий загрузчик сначала ждёт готовности Configuration: максимум 5 проверок с deadline 2 секунды и паузами 5 секунд; отдельного цикла повторов Web нет. См. [[Backend/GrpcServer]]. Внутренние gRPC-соединения идут по HTTP/2 без TLS внутри Docker-сети.
- Пользовательский access-токен проверяется локально. При истечении или отзыве Web запрашивает токены по refresh-cookie у Identity. Локальный кэш отзыва синхронизируется с Identity; см. [[Backend/SessionRevocation]].
- Большинство `/api/*`-маршрутов требует пользовательскую сессию. Неизвестный `/api/*` возвращает 404; остальные маршруты SPA обслуживаются fallback-маршрутом и требуют сессию, кроме публичных страниц ссылок.
- HTTP-клиент `files-upload` проксирует скачивание и просмотр файлов из Files со сроком по умолчанию (100 с, действует до получения заголовков). Legacy-прокси `POST /api/files/upload` отсутствует. Браузер управляет Upload 2.0 через `/api/files/uploads` и передаёт части прямо в Files через `/file-upload/`.
- Настройки, обслуживание, просмотр текста и поток загрузки описаны отдельно: [[Web/Settings]], [[Web/SystemUpdates]], [[Web/TextViewer]], [[Backend/ResumableUpload]].

## Очередь загрузок в шапке

`ClientApp/src/components/upload/UploadIndicator.tsx` показывает очередь из `ClientApp/src/hooks/useUploadManager.tsx`. В панели доступны кнопки с иконками и подсказками: очистить завершённые, приостановить/продолжить всю отправку, очистить всю очередь.

- `pauseAll` останавливает хэширование и передачу частей, сохраняя файлы и идентификаторы сессий. Новые файлы также остаются на паузе. `resumeAll` продолжает отправку через `ResumeUploadSession` по списку подтверждённых сервером частей; уже рассчитанный SHA-256 повторно не вычисляется. Серверная обработка и прикрепление уже переданных файлов продолжаются во время паузы.
- `clearAll` прерывает клиентские операции, отменяет незавершённые сессии и удаляет записи из IndexedDB. Поздние ответы не запускают следующие шаги и не восстанавливают удалённые записи. Уже загруженные файлы в облаке сохраняются.
- Статус `paused` сохраняется в `ClientApp/src/lib/uploadQueueStore.ts`. После перезагрузки страницы такой файл требует повторного выбора (`needs_file`), поскольку объект `File` в IndexedDB не сохраняется.
