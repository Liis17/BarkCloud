# Android — App

Parent: [[index]] · Авторизация: [[modules/android-auth]] · Upload 2.0: [[modules/android-upload2]] · See also: [[modules/shared-proto]] · [[api/identity-api]] · [[modules/backend-grpcserver]] · [[modules/ios-app]]

## Назначение

Нативный Android-клиент BarkCloud (Kotlin, Jetpack Compose, **Material 3 Expressive**). Достигнут функциональный паритет с [[modules/ios-app]]: первый запуск, вход+OTP, регистрация и сброс пароля, 5 табов как в iOS (**Галерея / Файлы / Альбомы(по умолчанию) / Корзина / Настройки**), облачные медиа с пагинацией, альбомы (CRUD), облачный файл-браузер (CRUD/перемещение/загрузка), умные разделы (`DynamicFolderApi`), Shared Files Hub (публичные ссылки/исходящие/входящие гранты), App Lock (биометрия/PIN на вход) и Vault (приватная папка), управляемый кеш оригиналов, foreground upload queue с Android 16 `Notification.ProgressStyle`, автозагрузка медиатеки через WorkManager, очистка локальных копий, Storage widget, deep links, системный share target, корзина, профиль/аватар/приватность/устройства, избранное. gRPC-связь со всеми микросервисами (Identity :8000, Users :8001, Files/Cloud/Album/DynamicFolder :8005) + HTTP-слой для upload/download/превью с проверкой TLS и отдельным разрешением самоподписанных сертификатов выбранного сервера.

## Реализованный функционал (паритет с iOS)

### Обновление: первый запуск и авторизация (2026-10-07)

14 шаблонов `Docs/New-design` перенесены в Compose: три приветствия, отдельный сервер, пароль/ключ доступа, четыре шага регистрации и завершение, три шага сброса; дополнительно оформлена 2FA. Новые компоненты используют роли M3, системную светлую/тёмную тему, Dynamic Color на Android 12+ и фирменную схему Android 11. Декор — Canvas/`graphics-shapes`, морфинг и движение через `graphicsLayer`; формы прокручиваются с IME и увеличенным шрифтом, ограничены 480 dp. Подробные контракты, восстановление pending-регистрации, TLS и результаты проверок: [[modules/android-auth]].

Проверка сохранённой сессии выявила гонку deep link с установкой вложенного графа `Scaffold`; переход теперь ждёт первую запись back stack.

Нативный Credential Manager пока не подключён: вкладка ключа доступа объясняет доступность и возвращает к паролю. Backend-контракты не менялись.

### Обновление: Material 3 Expressive — визуальный слой (2026-10-03)

Приведение UI к визуальному языку Android 16/17 (M3 Expressive), в рамках material3 1.4.0-alpha18:

- **Моушен**: переходы NavHost — fade through для табов (`MainScreen`/`RootNavGraph`), shared axis X для drill-in через helper `drillIn()` (`ui/navigation/NavMotion.kt`, токены spatial.default/effect.default). `AnimatedContent` на сегментах MediaTab/SharedHub, `AnimatedVisibility` для GlobalUploadBanner и оверлея AppLock (вход fade+scale, выход мгновенный — security), spring-анимация PIN-точек (`BarkMotion`), хаптики: клавиатура PIN (KEYBOARD_TAP) и свайпы корзины (CONFIRM).
- **Expressive-компоненты**: `LoadingIndicator` вместо экранных `CircularProgressIndicator` (в кнопках/бейджах остался CPI), `LinearWavyProgressIndicator` для детерминированных прогрессов (хранилище, кеш, очередь загрузок, баннер, операции LocalBrowser, ShareActivity), `HorizontalFloatingToolbar` для режима выделения галереи (вместо полноширинной кнопки), `LargeTopAppBar` (collapsing) на Настройках, `FilterChip` вместо AssistChip-переключателей (CacheSettings, UploadSettings, SmartFolderFormDialog — комбинатор/вид стали явным single-select).
- **Адаптивность**: `GridCells.Adaptive(120dp/180dp)` вместо `Fixed(3)`/`Fixed(2)` во всех гридах — колонки растут на планшетах/landscape без material3-adaptive.
- **Бренд-палитра**: `Color.kt` — фирменная тёплая схема (seed #9A4F1E), синхронизирована с веб-клиентом (`shared.css`), включая surface-container'ы и outline; на Android 12+ по-прежнему перекрывается Dynamic Color.
- **Система**: splash screen (`core-splashscreen` 1.2.0, `Theme.BarkCloud.Splash`, фон #FFF8F5) на MainActivity; `android:enableOnBackInvokedCallback=true` (predictive back); scrim-оверлеи переведены с хардкода `Color.Black` на роль `colorScheme.scrim`.
- **Мелочи**: захардкоженные строки UploadSettings/Settings вынесены в `strings.xml`; `contentDescription` для select-режима галереи, play-кнопок вьюеров, back-стрелок; шаблонные purple/teal цвета удалены из `colors.xml`. Лаунчер-иконка — осознанно оставлена системной заглушкой (бренд-иконки нет).

Не сделано (нет в alpha18 / вне скоупа): Flexible-топбары, SplitButton, ButtonGroup, карусели, material3-adaptive (NavigationSuiteScape, List-Detail), Material Symbols, бренд-иконка.

### Обновление: сессия, очередь и автозагрузка

- **Сессия**: `GlobalParam` использует `TokenStore` — единый зашифрованный blob в credential-protected storage, ключ AES-GCM хранится в Android Keystore. На первом запуске мигрируются прежние `EncryptedSharedPreferences`; `TokenRefresher` обновляет access-token за 60 секунд до истечения и сериализует параллельные refresh-запросы. `Auth` и `CreateToken` уходят без старого auth-header; невалидный refresh очищает сессию и возвращает приложение на login.
- **Очередь загрузок (Upload 2.0, с 2026-10-03 — см. [[modules/android-upload2]])**: Room-база `barkcloud-local.db` (v2) хранит `UploadJob` с сессией Upload 2.0 (`sessionId/idempotencyKey/partSize`) и `MediaCloudState`. Фазы: `QUEUED → HASHING → CREATING_SESSION → UPLOADING → COMPLETING → PROCESSING → ATTACHING → COMPLETED` (+`UPLOADED_NOT_ATTACHED`, `PAUSED` для backup). Возобновление опирается на серверный список частей (`ResumeUploadSession`), не на локальный прогресс; до **4 файлов параллельно**, части файла последовательно. Источник всегда копируется в staging (`filesDir/upload_queue`), включая backup. Ручные, gallery, album, share и backup-загрузки обслуживает один foreground `UploadWorker`; data plane — `PUT /file-upload/{session}/parts/{n}` на nginx :443 (`FILES_UPLOAD_BASE`). Аватар — legacy V1 multipart.
- **Маршрутизация**: gallery, media picker, backup и системный Share target используют `route_by_media_kind` → серверные «Фото»/«Видео»/«Другие документы». Upload из облачного браузера остаётся в выбранной папке; upload в альбом сначала размещается в системной папке, затем добавляется в альбом.
- **Автозагрузка**: `AutoUploadWorker` только сканирует MediaStore, кеширует SHA-256 и версию (`id`, тип, размер, дата изменения) в Room и подаёт максимум 20 backup-задач в общую очередь. Политика в настройках: Wi‑Fi (по умолчанию), любая сеть или off; off ставит backup-задачи на паузу, не трогая ручные. При открытом приложении MediaStore observer с debounce запускает повторный scan; в фоне действует hourly WorkManager.
- **Галерея/кеш**: статусы файлов устройства разделены на checking/not-in-cloud/queued/uploading/in-cloud/error; бейдж «в облаке» появляется только после upload+attach. Перед `MediaStore.createDeleteRequest` наличие перепроверяется на сервере. Device и cloud grids сгруппированы по дате съёмки/создания. Coil имеет выделенный 256 MiB disk cache для preview/аватаров; originals остались в управляемом LRU-кеше. В настройках профиля `ProfileViewModel` для собственного аватара сначала выбирает `profile_picture`, а `profile_picture_preview` оставляет fallback.

> Реализовано фазами 1–4F (2026-05-27). Весь код компилируется (`./gradlew :app:assembleDebug`). Подробности и решения — в авто-памяти `android-ios-parity` и плане `bubbly-coalescing-hedgehog.md`.

- **Material 3 Expressive** (`ui/theme/`): `MaterialExpressiveTheme` + `MotionScheme.expressive()`, фирменный seed поверх expressive-схемы + dynamic color (Android 12+). Требует material3 **1.4.0-alpha18** (форс в `app/build.gradle.kts` через `resolutionStrategy`; в стабильной 1.4.0 Expressive-API `internal`).
- **gRPC/сеть** (`grpc/`, `net/`): `GrpcManager` (мульти-эндпоинт, кэш каналов; стабы Identity/Users/Files/Cloud/Album/DynamicFolder), `GrpcEndpoint.normalizedFileDownloadURL`, `InsecureTls` (общий trust-all), `InsecureHttp` (OkHttp), `FileTransferService` (multipart upload стримингом по Uri / download). Coil настроен на trust-all OkHttp (`OkHttpNetworkFetcherFactory`) для превью с :8005.
- **Данные** (`data/cloud/`, `data/users/`, `data/cache/`, `data/gallery/`, `data/upload/`): `CloudModels` (MediaAsset/Album/Trash/Favorite…), `DynamicFolderModels`, `CloudRepository` (медиа/каталоги/корзина/избранное/upload), `AlbumRepository`, `DynamicFolderRepository`, `UserRepository`, `FileCacheService`/`FileCacheSettings`, `AutoUploadSettings`/`AutoUploadWorker`/`AutoUploadScheduler`, `UploadQueueStore`/`UploadWorker`/`UploadNotification`, `SessionManager` (logout+очистка). Зарегистрированы в `BarkCloudApplication`.
- **UI-экраны** (`ui/`): `gallery/` (MediaStore+SHA256-бейдж «в облаке» через `CheckFileHashes`, автозагрузка, системное удаление локальных копий через `MediaStore.createDeleteRequest`), `media/`+`albums/` (сегменты Фото/Видео/Альбомы, cursor-пагинация, CRUD альбомов, контекстное меню избранного), `files/` (`CloudBrowserScreen` + `CloudMovePicker`, cursor-пагинация файлов по 50 с автоподгрузкой у конца списка и нижним индикатором, секция умных разделов на корне), `smartfolders/` (`SmartFolderDetailScreen`, `SmartFolderFormDialog`), `trash/` (свайпы restore/delete-forever, empty), `settings/` (профиль/аватар/приватность/устройства/кеш/выход/удаление), `favorites/`. Общие компоненты — `ui/components/` (`RemoteImage`, `MediaThumb`, `CloudMediaViewer`, `ComingSoonScreen`, `TextInputDialog`, `rememberRemoteOpener`).
- **Widgets/deep links**: `widgets/StorageWidgetProvider` + `StorageWidgetBridge` (RemoteViews, snapshot used/limit из `ProfileViewModel`), deep links `barkcloud://gallery|files|albums|media|trash|settings|vault` (`vault` резолвится напрямую на вложенный роут `settings/vault` — NavHost ищет route глобально по графу).
- **Share target**: `ShareActivity` принимает `ACTION_SEND`/`ACTION_SEND_MULTIPLE` из системного Sharesheet и ставит переданные `EXTRA_STREAM` URI в foreground upload queue.
- **Навигация** (`ui/main/`): 5 табов через вложенные графы (per-tab back-stack), pill-NavigationBar; sign-out проброшен `RootNavGraph → MainScreen → SettingsScreen`.
- **Shared Files Hub** (`ui/shared/`): 3 сегмента на роуте `files/shared` — «Мои публичные» (`ListMyShares`/`ListMyFolderShares`/`ListMyAlbumShares`, без пагинации, revoke оптимистичен, «Поделиться ссылкой» строит `{filesHost}/s|f|al/{token}` и открывает system share sheet), «Я поделился» (`ListMyOutgoingSharesAll` cursor-paginated + `ListMyOutgoingFolderShares` best-effort, группировка по файлу/папке, резолв получателей через `UserRepository.listByIds`), «Мне доступны» (`ListSharedWithMe` cursor-paginated + `ListSharedFoldersWithMe` best-effort, скачивание во временный кэш + `ACTION_VIEW`, без revoke). Навигация по чужой папке — отдельный read-only экран `SharedFolderBrowserScreen` (роут `files/shared/folder`, `ListSharedDirectory`). `SharedRepository` — обёртка над шаринг-RPC `CloudApi`, отдельно от `CloudRepository`. `UserRepository.listByIds` резолвит батч параллельными `GetUser` — `ListByIds` в `UsersServerApi` (inter-service), клиенту недоступен.
- **App Lock** (`ui/applock/` + `data/`): полноэкранный `AppLockScreen` — оверлей (`Box` поверх `NavHost` в `RootNavGraph.kt`, не route), авто-биометрия при появлении, PIN-клавиатура (`PinKeypad`/`PinDots`) как fallback. `AppLockManager` (`DefaultLifecycleObserver` на `ProcessLifecycleOwner`) держит `shouldShowLock: StateFlow<Boolean>` с 30-сек grace-period после ухода в фон (зеркалит iOS `scenePhase`). `AppLockStore` — PIN хранится как PBKDF2-HMAC-SHA256 (100k итераций) хэш+соль, зашифрованные AES-256-GCM ключом из Android Keystore (тот же паттерн, что `TokenStore`), сравнение `MessageDigest.isEqual`. 3 неверных попытки → `SessionManager.resetLocalState()` (логаут, трактуется как "wipe"). Настройки — `AppLockSettingsScreen` (роут `settings/applock`), toggle требует биометрию/PIN устройства. PIN **не сбрасывается** при логауте (в отличие от Vault).
- **Vault** (`ui/vault/` + `data/vault/`): приватная папка — **чисто клиент-локальная** (сервер не знает о «приватности», это ссылки на обычные облачные файлы), зеркалит iOS-архитектуру. `VaultStore` хранит JSON-список `VaultItem` (file_id+превью+isVideo) в обычном `SharedPreferences` (без Keystore-шифрования — защита от чужого взгляда, не от компрометации устройства). `VaultScreen` (роут `settings/vault`) — грид по образцу `FavoritesScreen`, per-session biometric-гейт через общий `BiometricGate` (релок на `ON_STOP` жизненного цикла экрана, без grace-period, в отличие от App Lock). Точка входа «Добавить в vault» — контекстное меню `MediaGridScreen` (единственная в этой итерации). `VaultStore.removeAll()` подключен к `SessionManager.resetLocalState()` — вайпается при логауте.
- **BiometricGate** (`data/BiometricGate.kt`) — общая обёртка над `androidx.biometric.BiometricPrompt` (`BIOMETRIC_STRONG or DEVICE_CREDENTIAL`, допускает device PIN/паттерн как фолбэк) для App Lock и Vault.

Первый запуск/auth проверены инструментально на Android 11/16 и входом на production с разрешённым тестовым аккаунтом (см. [[modules/android-auth]]). Реальное поведение self-signed превью/upload и остальных основных сценариев требует отдельной проверки. Ниже — описание исходного каркаса (вход + локальный браузер), частично устарело.

## Расположение

`Android/BarkCloud.Android/`

## Структура (фактическая)

```
app/src/main/java/com/barkfluff/BarkCloud/
├── BarkCloudApplication.kt   — ручной service locator + Coil ImageLoader (VideoFrameDecoder)
├── MainActivity.kt           — edge-to-edge, setContent { BarkCloudTheme { RootNavGraph() } }
├── ShareActivity.kt          — системный target «Поделиться» → staging URI в upload queue
├── data/
│   ├── GlobalParam.kt        — TokenStore (AES-GCM/Keystore), sessionActive, сроки access/refresh токенов
│   ├── AuthRepository.kt     — IdentityApi.Auth → AuthResult (Success/OtpRequired/InvalidCredentials/OtherError)
│   ├── AuthFlowRepository.kt — регистрация, pending password, сброс, ошибки/retry-after
│   ├── PendingRegistrationStore.kt — отдельный AES-GCM/Keystore refresh, привязанный к серверу
│   ├── OnboardingStore.kt    — флаг завершения трёх страниц приветствия
│   ├── NotificationPermissionRequests.kt — запрос разрешения от действия загрузки
│   ├── AppLockStore.kt       — Keystore-хранилище PIN-хэша (PBKDF2+AES-256-GCM)
│   ├── AppLockManager.kt     — ProcessLifecycleOwner-наблюдатель, 30s grace-period, shouldShowLock: StateFlow
│   ├── BiometricGate.kt      — обёртка над BiometricPrompt (App Lock + Vault)
│   ├── vault/VaultStore.kt   — JSON-список VaultItem в SharedPreferences (без шифрования)
│   ├── cache/
│   │   ├── FileCacheSettings.kt — SharedPreferences: лимит кеша, автоочистка, lastSweep
│   │   └── FileCacheService.kt  — кеш оригиналов в cache/BarkCloudFiles/originals, LRU/age очистка
│   └── cloud/
│       ├── DynamicFolderModels.kt     — модели умных разделов и страниц элементов
│       ├── DynamicFolderRepository.kt — DynamicFolderApi: list/create/update/delete/listItems
│       ├── SharedModels.kt            — модели шаринга (PublicShareItem/OutgoingShareGroup/SharedWithMeEntry/…)
│       └── SharedRepository.kt        — CloudApi: публичные ссылки/гранты пользователям/чужая папка
│   └── gallery/
│       ├── AutoUploadSettings.kt  — SharedPreferences-флаг автозагрузки + последний результат
│       ├── AutoUploadScheduler.kt — WorkManager unique periodic/one-time jobs
│       └── AutoUploadWorker.kt    — MediaStore scan → SHA256 → CheckFileHashes → upload missing
│   └── upload/
│       ├── UploadQueueStore.kt   — staged-файлы в files/upload_queue + SharedPreferences queue JSON
│       ├── UploadScheduler.kt    — unique one-time WorkManager job
│       ├── UploadWorker.kt       — foreground dataSync upload queue processor
│       └── UploadNotification.kt — progress notification; Android 16+ ProgressStyle
├── grpc/
│   ├── GrpcManager.kt         — каналы всех сервисов (OkHttp), TLS-политика выбранного сервера
│   ├── AuthInterceptor.kt     — заголовок x-auth-token (динамически, без base64)
│   ├── ClientMetadataInterceptor.kt — x-device-id/name, x-os-name, x-app-name/version, x-ip-address (base64 NO_WRAP)
│   ├── GrpcError.kt           — GUID из x-error-code, нормализация checked/runtime gRPC ошибок
│   ├── ServerSettings.kt      — ServerConfig, валидация, миграция TLS, сохранение после проверки
│   ├── ServerConnectionProbe.kt — параллельный публичный reflection ListServices, deadline 5 s
│   └── AuthErrorCodes.kt      — GUID-коды OTP_REQUIRED / INVALID_CREDENTIALS
├── ui/
│   ├── navigation/RootNavGraph.kt — welcome/server/login/register/reset/main, App Lock и deep links
│   ├── login/                 — пароль/подготовленный ключ доступа, OTP, rate-limit и resend
│   ├── onboarding/            — WelcomeScreen (3 страницы, без пропуска)
│   ├── server/                — ServerScreen + ServerViewModel, три сервиса и TLS-политика
│   ├── auth/                  — Registration/Reset ViewModel, экраны, общие поля и Canvas-декор
│   ├── main/                  — MainScreen (Scaffold + вложенный NavHost), MainDestination (5 табов), MainBottomBar
│   ├── applock/                — AppLockScreen (биометрия+PIN keypad), PinDots/PinKeypad (internal, переиспользуются в settings)
│   ├── vault/                  — VaultScreen + VaultViewModel (грид, per-session biometric-гейт)
│   ├── settings/              — настройки профиля/приватности/устройств + CacheSettingsScreen + AppLockSettingsScreen
│   ├── shared/                 — Shared Files Hub: SharedHubScreen + 3 таба + SharedFolderBrowserScreen
│   ├── smartfolders/           — содержимое умного раздела и форма правил
│   ├── screens/PlaceholderScreen.kt — заглушка табов Photos/Videos/Shared/Settings
│   └── theme/                 — Color, Shape, Theme, Type (Material 3)
├── widgets/
│   ├── StorageWidgetBridge.kt   — snapshot квоты в SharedPreferences + update AppWidgetManager
│   └── StorageWidgetProvider.kt — RemoteViews Home Screen виджет хранилища
└── files/                     — локальный файл-браузер (см. ниже)
app/src/main/proto/            — синхронизируется из Shared/BarkCloud.Proto (gradle task syncSharedProto)
```

## Service locator (`BarkCloudApplication`)

Зависимости создаются вручную в `onCreate` (без Hilt/Koin), доступны через `applicationContext as BarkCloudApplication`:

- `globalParam: GlobalParam` — единый TokenStore blob (AES-GCM/Android Keystore) и состояние сессии; старые EncryptedSharedPreferences мигрируются.
- `grpcManager: GrpcManager` — каналы/стабы gRPC; в конструктор передаётся `ClientMetadataInterceptor.create(this)`.
- `authRepository: AuthRepository` — авторизация.
- `authFlowRepository: AuthFlowRepository` — регистрация, зашифрованный pending refresh и сброс пароля.
- `localFileRepository: LocalFileRepository` — доступ к локальной ФС.

Класс также реализует `SingletonImageLoader.Factory` — настраивает Coil 3 с `VideoFrameDecoder` (превью видео) и crossfade. В `onTerminate` вызывает `grpcManager.shutdown()`.

## Модуль `files/` — локальный браузер

```
files/
├── domain/
│   ├── FsEntry.kt   — sealed (Directory{childCount} / File{sizeBytes, mimeType})
│   └── FsSort.kt    — enum сортировки + applySort (папки всегда сверху)
├── data/
│   ├── LocalFileRepository.kt — list/createDir/... поверх java.io.File (Dispatchers.IO, Result<>)
│   ├── FileShareHelper.kt     — шаринг через FileProvider + ACTION_SEND
│   ├── MimeIcon.kt            — определение MIME и иконки по расширению
│   └── StoragePermission.kt   — MANAGE_EXTERNAL_STORAGE, externalRoot
└── ui/
    ├── FilesRootScreen.kt / FilesRootViewModel.kt — корень: запрос разрешения + облако/общие файлы + умные разделы (`DynamicFolderApi`)
    ├── LocalBrowserScreen.kt / LocalBrowserViewModel.kt — навигация по каталогам
    ├── FsRowItem.kt, FormatUtils.kt, PickFolderDialog.kt, rememberThumbnailModel.kt
```

Умные разделы грузятся через `DynamicFolderApi.ListDynamicFolders`; пользовательские разделы можно создать/изменить/удалить, системные только открыть. Содержимое раздела (`ListDynamicFolderItems`) отображается сеткой превью с cursor-пагинацией и просмотром через общий `CloudMediaViewer`.

## Автозагрузка медиатеки

Первый Android-аналог iOS `BackupManager`: переключатель в `GalleryScreen`
сохраняет `AutoUploadSettings.enabled` и планирует `AutoUploadWorker` через
WorkManager. При включении ставятся:
- one-time job `barkcloud_auto_upload_once` для немедленного запуска;
- periodic job `barkcloud_auto_upload_periodic` раз в час с constraint
  `NetworkType.CONNECTED`.

`AutoUploadWorker` создаёт сетевой стек без DI (`GlobalParam` → `GrpcManager` →
`FileTransferService` → `CloudRepository`), проверяет валидный refresh token,
читает до 200 последних фото/видео из `MediaStore`, считает SHA256 существующим
`MediaHasher`, пачками вызывает `CheckFileHashes`, и загружает отсутствующие через
`CloudRepository.uploadFile(uri, name)`. Worker работает как foreground data-sync
work и обновляет progress notification через `UploadNotification`; Android 16+
получает `Notification.ProgressStyle` (Live Updates/progress chip), старые версии —
обычный progress bar notification. При выходе из аккаунта `SessionManager`
отменяет обе unique work-задачи.

## Foreground upload queue

Ручные загрузки из `GalleryViewModel`, `MediaGridViewModel`, `AlbumDetailViewModel`,
`CloudBrowserViewModel` и входящие файлы из `ShareActivity` больше не грузятся напрямую из UI. Они копируют
исходный `content://` URI в app-private staging (`files/upload_queue`) через
`UploadQueueStore.enqueue(...)` (Room-очередь) и запускают unique one-time `UploadWorker`.

**Upload 2.0 (актуально, с 2026-10-03):** воркер гоняет до 4 файлов параллельно
через возобновляемые сессии (`create → PUT части → complete → poll ready → attach`),
части файла последовательно; восстановление после рестарта — через серверный
`ResumeUploadSession`. Подробная карта — [[modules/android-upload2]].

Для cloud browser сохраняется `directoryId`, поэтому файл после upload прикрепляется к выбранной папке; для загрузки в альбом сохраняется
`albumId`, и после получения `fileId` worker вызывает `AlbumRepository.addItems`. Worker использует
`ForegroundInfo(..., ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)` и требует
permissions `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC`, `POST_NOTIFICATIONS`.
`MainActivity` и `ShareActivity` запрашивают `POST_NOTIFICATIONS` на Android 13+ по событию первого действия загрузки или включения автозагрузки; на запуске запроса нет.

## Очистка локальных копий

`GalleryScreen` показывает кнопку «Удалить копии с устройства», когда текущая сетка
уже определила через `CheckFileHashes`, что локальные фото/видео есть в облаке.
Удаление выполняется не напрямую, а через системный `MediaStore.createDeleteRequest`
и `ActivityResultContracts.StartIntentSenderForResult`: Android показывает
пользователю confirmation dialog, после успешного результата `GalleryViewModel`
перечитывает MediaStore.

## Виджет и deep links

Storage widget реализован стандартным `AppWidgetProvider`/`RemoteViews` без Glance:
`ProfileViewModel.load()` после `GetUserStorageInfo` вызывает
`StorageWidgetBridge.update(used, limit)`, bridge сохраняет snapshot в
`SharedPreferences` и обновляет все экземпляры `StorageWidgetProvider`. Виджет
показывает процент, занято/лимит и progress bar; если snapshot ещё нет — просит
открыть приложение. Тап по виджету открывает `barkcloud://settings`.

`MainActivity` принимает `barkcloud://...` через intent-filter и передаёт URI в
`RootNavGraph`/`MainScreen`. Поддержанные targets: `gallery`, `files`, `albums`,
`media`, `trash`, `settings`; сейчас они переключают табы, без открытия конкретного
файла/альбома.

## Share target

`ShareActivity` зарегистрирован в `AndroidManifest.xml` для `ACTION_SEND` и
`ACTION_SEND_MULTIPLE` (`image/*`, `video/*`, `application/*`, `text/*`). Activity
извлекает `Intent.EXTRA_STREAM` URI, проверяет наличие валидного refresh token,
копирует файлы в `UploadQueueStore` и запускает `UploadWorker`. После staging экран
можно закрыть: фактическая передача идёт в foreground WorkManager job с progress
notification.

## gRPC-метаданные клиента

Сервер ([[modules/backend-grpcserver]], `RequestContextInterceptor`) читает метаданные и на части эндпоинтов Identity **требует** заголовки (значения в base64, кроме токена):

- `x-auth-token` — JWT, **без** base64. Добавляет `AuthInterceptor` динамически на каждый запрос.
- `x-device-id`, `x-device-name`, `x-os-name`, `x-app-name`, `x-app-version`, `x-ip-address` — статичны (считаются один раз), base64 `NO_WRAP` (перенос строки сломал бы `Convert.FromBase64String` на сервере). Добавляет `ClientMetadataInterceptor`.

Оба цепляются в `GrpcManager.identityStub()`. Публичные signup/reset/username стабы получают `ClientMetadataInterceptor`, установка первого пароля — явно переданный pending access-token. Адреса читаются из `ServerSettings`: Identity 8000, Users 8001, Files 8005. Отдельный экран сервера сохраняет конфигурацию только после успешного reflection всех трёх API. По умолчанию TLS строгий; `allowSelfSigned` разрешает исключение цепочки только для выбранного хоста с сохранённой проверкой имени. HTTP/Coil/загрузки/workers используют общий `Call.Factory`, клиентская генерация пересоздаётся при смене конфигурации. Подробнее: [[modules/android-auth]].

Коды ошибок-GUID (`AuthErrorCodes`) приходят в трейлере `x-error-code`; `AuthRepository` транслирует их в `AuthResult`.

## Конфигурация

| Параметр | Значение |
|---------|---------|
| `applicationId` / `namespace` | `com.barkfluff.BarkCloud` |
| `minSdk` | 30 |
| `compileSdk` / `targetSdk` | 36 |
| `versionCode` / `versionName` | 1 / 1.0 |
| Java / jvmTarget | 11 |
| `BuildConfig.IDENTITY_API_ADDRESS` | `https://cloud.barkfluff.com:8000` (дефолт `ServerSettings`, переопределяется на экране сервера) |

Plugins: `android.application`, `kotlin.android`, `kotlin.compose`, `protobuf` (через version catalog `libs`).

Manifest: разрешения `INTERNET` и `MANAGE_EXTERNAL_STORAGE`; `FileProvider` с authority `${applicationId}.fileprovider` (пути в `res/xml/file_paths.xml`).

## proto / gRPC-сборка

Gradle-таск `syncSharedProto` копирует `**/*.proto` из `Shared/BarkCloud.Proto` в `app/src/main/proto`, откуда `protobuf-gradle-plugin` генерирует java+kotlin **lite** + grpc + grpckt. От таска зависят все `generateProto*`/`extract*Proto`. `resolutionStrategy` пинит `kotlin-stdlib` к версии компилятора (Coil тянет более новый stdlib).

## Зависимости (ключевые)

Compose BOM + material3 + material-icons-extended, navigation-compose, lifecycle-viewmodel/runtime-compose, WorkManager, `androidx.security:crypto`; protobuf javalite + kotlin-lite, grpc okhttp/protobuf-lite/stub/kotlin-stub; kotlinx-coroutines-android; Coil compose + video. Полный список — `gradle/libs.versions.toml`.

## Сборка

```bash
cd Android/BarkCloud.Android
./gradlew assembleDebug
```
