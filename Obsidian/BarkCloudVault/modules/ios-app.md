# iOS — App

Parent: [[index]]

> 🛡 Методология проверки клиента (безопасность · производительность · качество кода):
> `Docs/audit/IOS_SECURITY_PERFORMANCE_AUDIT.md`.

## Назначение

Нативный iOS-клиент BarkCloud (SwiftUI, Swift 5, iOS 18+). gRPC через grpc-swift 2. Login+OTP, серверная интеграция профиля/медиа/облака. 5-табовая навигация: **Галерея** (медиатека устройства, PhotoKit), **Файлы** (устройство + облако + общие), **Альбомы** (default; облачные Фото/Видео/Альбомы), **Корзина** (облако), **Настройки**.

## Расположение

`Ios/BarkCloud/`
- `BarkCloud.xcodeproj/` — Xcode-проект (filesystem-synchronized group → файлы из `BarkCloud/` подхватываются автоматически).
- `BarkCloud/` — корень исходников.
- `sync_proto.sh` — скрипт для Run-Script build phase, синхронизирует `.proto` из `Shared/BarkCloud.Proto/`.

## Текущая структура

> ⚠️ **С 2026-06-03 (Этап 0 macOS, [[macos-drive]]) сетевой слой вынесен в общий SwiftPM-пакет
> `BarkCloudKit` (`Mac/BarkCloudKit/`).** Физически перенесены (`git mv`): `Networking/`
> (кроме upload-файлов), `Session/SessionStore.swift`, `Data/Cloud|Auth|Users/*`, и proto
> (`Generated/`). В iOS-коде теперь `import BarkCloudKit`; API этих типов — `public`. В дереве
> ниже эти файлы помечают **логическую** структуру, но живут в пакете. Остаются в iOS-таргете:
> `Networking/{BackgroundUploadCoordinator,UploadConstants,UploadLiveActivityController,
> UploadProgressObserver}.swift`, `Data/Cache/{UploadJob,UploadQueueStore}.swift` (см.
> [[ios-background-upload]]) и `Data/Cloud/CloudRepository+BackgroundUpload.swift` (фоновая
> загрузка — расширение над пакетным `CloudRepository`). App Group id для `ServerConfig`/
> `SessionStore` в пакете задаёт `BarkCloudAppGroup` (на macOS — своя ветка).

```
BarkCloud/
├── App/
│   ├── BarkCloudApp.swift          @main, инжектит AppEnvironment в RootView
│   ├── AppEnvironment.swift        @Observable service locator (serverConfig, sessionStore, grpcManager, authRepository, localFileRepository, fileTransfer, userRepository, cloudRepository, albumRepository)
│   ├── ServerConfigStore.swift     @Observable обёртка над ServerConfig (App Group UserDefaults): config + isConfigured + save(_:) — гейт первого запуска (см. [[#Server Setup]])
│   └── RootView.swift              gate: !serverConfig.isConfigured ? ServerSetup : (!sessionExpired && (hasValidRefreshToken || isAuthenticated) ? Main : Login)
├── Session/
│   └── SessionStore.swift          Keychain (kSecClassGenericPassword, service "com.barkfluff.BarkCloud.tokens"); snapshot()/saveRefreshedAccessToken()/invalidate(), наблюдаемый флаг sessionExpired
├── Networking/                     gRPC: GrpcManager (actor) + интерсепторы
│   ├── GrpcManager.swift           multi-endpoint поверх **ServerConfig** (self-hosted адреса из App Group UserDefaults, см. [[#Server Setup]]): per-service host+port Identity/Users/Files(Cloud/Album), TLS allowSelfSigned, кэш GRPCClient по ключу `host:port`, стабы identity/users/files/cloud/album; **проактивное авто-обновление access-токена** (Identity.CreateToken, сериализовано через refreshTask). `GrpcEndpoint` — статические computed-аксессоры над `ServerConfig.current` (хосты/порты/scheme/filesWebBase/webHost); до настройки — `ServerConfig.production` дефолты
│   ├── InsecureURLSession.swift    URLSession, доверяющий self-signed TLS (для HTTP upload/download и превью)
│   ├── FileTransferService.swift   FilesApi (GetUploadUrl/GetTempDownloadUrl/StorageInfo) + HTTP multipart upload (поле `file`) / download оригинала
│   ├── CloudErrorCodes.swift       GUID-коды доменных ошибок Files/Users + domainErrorMessage(_:)
│   ├── AuthInterceptor.swift       x-auth-token — токен берёт у GrpcManager по имени метода (с проактивным refresh; публичные Auth/CreateToken → nil, чтобы стейл-токен не ловил 401 от auth-middleware)
│   ├── XAppInterceptor / XDeviceInterceptor / XIpInterceptor / XOsInterceptor — device-метаданные
│   ├── Base64Header.swift          base64-кодирование значений заголовков
│   ├── AuthErrorCodes.swift        GUID-коды OTP_REQUIRED / INVALID_CREDENTIALS
│   └── GrpcError.swift             извлечение x-error-code из trailing-metadata
├── Data/Auth/
│   ├── AuthRepository.swift        IdentityApi.Auth, сохранение токенов в SessionStore (+ сброс sessionExpired). Обновление access-токена (CreateToken) — внутри GrpcManager
│   └── AuthResult.swift            enum: success / otpRequired / invalidCredentials / otherError
├── Data/Users/
│   └── UserRepository.swift        UsersApi: профиль, имя/юзернейм/bio, приватность, устройства, удаление аккаунта, аватар (через FileTransferService)
├── Data/Cloud/
│   ├── CloudModels.swift           доменные модели UI: MediaAsset, MediaPage, CloudDirectory, CloudFileEntry, AlbumCard, PathCrumb, **CloudFileMetadata** (плоская копия `FileMetadataInfo` с optional-полями) (+ Timestamp.date)
│   ├── CloudRepository.swift       CloudApi: ListUserMedia, ListDirectoryDetailed, GetPath, CRUD папок/записей, uploadFile, **getFileMetadata(fileID:)** (nil при `has_metadata=false`)
│   └── AlbumRepository.swift       AlbumApi: список/содержимое альбомов, create/update/delete, add/remove items
├── Data/Cache/                     **постоянный дисковый кеш файлов** ([[ios-file-cache]]): CacheVariant, CachedFileEntry (SwiftData @Model), FileCacheService (actor), FileCacheSettings
├── Features/
│   ├── ServerSetup/                ServerSetupScreen — ввод адресов self-hosted сервера на первом запуске (см. [[#Server Setup]])
│   ├── Login/                      LoginScreen + LoginUiState + LoginViewModel (логин/пароль + OTP; внизу ссылка «Настройки сервера» → лист ServerSetupScreen)
│   ├── Main/                       MainScreen (TabView, 5 табов: Галерея/Файлы/Альбомы(default)/Корзина/Настройки), MainDestination
│   ├── Gallery/                    GalleryScreen+VM (медиатека устройства PhotoKit: сетка фото+видео, выбор, загрузка в облако), DeviceMediaViews (PHImageManager-загрузчик + ячейка + полноэкранный просмотр фото/видео), DeviceAssetResource (общее чтение оригинала+SHA256), CloudPresenceTracker (индикация «уже в облаке»), DeviceAssetPickerScreen (кастомный пикер загрузки — замена PhotosPicker)
│   ├── Shared/                     RemoteImage (self-signed AsyncImage-замена + NSCache; cache-aware вариант `RemoteImage(fileId:variant:url:)` и `FallbackRemoteImage(fileId:urls:)` тянут байты через дисковый кеш [[ios-file-cache]]), FilePreviewController/RemoteFilePreviewScreen (QuickLook; оригинал через FileCacheService.loadFile(.original)), MediaThumb (fileId + previewWidth) + SquareThumbClip (квадратная обрезка fill-картинки с корректным хит-тестом), ComingSoonScreen (универсальная заглушка «скоро»), BarkMascot/BarkRefreshHeader/BarkRefreshable (фирменный pull-to-refresh — **полностью свой, без системного `.refreshable`**, поэтому в зазоре нет ни системного спиннера, ни подложки: видна только пиксель-арт оранжевая лиса в Canvas — сидит ровно анфас, пушистый хвост справа виляет вверх-вниз непрерывным сдвигом по синусу; виляние идёт и при вытягивании, и при обновлении — TimelineView `.animation` с paused: `!(isRefreshing || progress > 0.001)`, масштаб появления ведётся от `pullProgress`. Жест: `onScrollGeometryChange` → `pullProgress`, `onScrollPhaseChange` → при отпускании (`.idle`/`.decelerating`) и `pullProgress >= 1` запускает обновление; во время обновления контент опускается на `refreshGap` через `.contentMargins(.top,…,for:.scrollContent)`, чтобы лиса была в чистом зазоре; **критично #1:** `pullProgress` считается как `max(0, -(contentOffset.y + contentInsets.top))/threshold` — именно `+ contentInsets.top`, потому что в покое `List` (TrashScreen) репортит `contentOffset.y == -contentInsets.top` (инсет навбара), и без поправки лиса висела бы постоянно; `ScrollView` (остальные экраны) покоится около 0, поэтому там баг не проявлялся; **критично #2:** прогресс/флаг обновления и сама задача (`Task`) хранятся в `@Observable BarkRefreshState`, а НЕ в `@State` модификатора — `body(content:)` читает только `isRefreshing` (редкий тогл для `contentMargins`), но НЕ `pullProgress`, поэтому прокрутка не пересобирает модификатор; задача обновления живёт в state-объекте и перерисовками view не отменяется (иначе рвался бы gRPC-запрос → «the transport threw an unexpected error»))
│   ├── Settings/                   SettingsScreen + ProfileViewModel (профиль/аватар/хранилище/выход/удаление), EditProfileScreen, PrivacySettingsScreen, DevicesScreen, CacheSettingsScreen + CacheSettingsViewModel (раздел «Кеш»: размер/записи/лимит/очистка — [[ios-file-cache]])
│   ├── Trash/                      TrashScreen+VM (корзина облака: ListTrash + cursor-пагинация, restore/delete-forever свайпом, EmptyTrash)
│   ├── Media/                      таб «Альбомы»: CloudMediaScreen с переключателем Фото/Видео/Альбомы
│   │   ├── MediaKind.swift         enum { photo, video }: titleKey, emptyKey, isVideo
│   │   ├── MediaItem.swift         модель (id=file_id, thumbnailURL?, isVideo, fileName) + init(asset:) + placeholders
│   │   ├── MediaTabScreen.swift    CloudMediaScreen: 3-сегментный переключатель → MediaGridScreen(.photo/.video) / AlbumsGridScreen(nil)
│   │   ├── MediaGridViewModel.swift @Observable: ListUserMedia + cursor-пагинация + загрузка + мультивыбор (selection/isSelecting/isProcessing/deleteDone/deleteTotal): deleteSelected (последовательно DeleteUserMedia(file_id) с прогрессом), addSelectedToAlbum, createAlbumAndAddSelected
│   │   ├── MediaGridScreen.swift   LazyVGrid 3 кол. (MediaThumb), загрузка через кастомный DeviceAssetPickerScreen (бейджи «уже в облаке»), полноэкранный просмотр; кнопка «Выбрать» → мультивыбор + нижняя панель без фона (Удалить — подтверждение поповером над кнопкой / В альбом)
│   │   └── Albums/                 AlbumsViewModel, AlbumsGridScreen (kind: MediaKind? — nil=без фильтра), AlbumDetailScreen+VM (items, обложка, add/remove), AlbumPickerSheet (выбор альбома + «создать новый»)
│   └── Files/                      файл-браузер (локальный + облачный + «Общие файлы»→ComingSoonScreen)
│       ├── Domain/                 FsEntry, FsSort
│       ├── Data/                   LocalFileRepository (actor), FileShareHelper, MimeIcon, StoragePermission
│       └── UI/                     FilesRootScreen/ViewModel (вход в облако), CloudBrowserScreen/ViewModel/UiState (навигация+CRUD+upload), LocalBrowserScreen/ViewModel, FsRowItem, FormatUtils, PickFolderDialog, ThumbnailLoader
├── Theme/
│   ├── AppColors.swift             SwiftUI semantic colors (Color.primary/secondary/accentColor)
│   ├── AppTypography.swift         Material 3 size scale через Font.system(size:weight:)
│   └── BarkCloudTheme.swift        ViewModifier с .tint(AppColors.accent)
├── Resources/
│   └── Localizable.xcstrings       Все строки, sourceLanguage = "ru"; локализации ru/en/de (см. [[#Локализация]])
├── Generated/Proto/                сгенерённые стабы: {identity,users,files,shared}_api.{pb,grpc}.swift
└── Assets.xcassets/                AccentColor, AppIcon (от стартера)
```

Вне группы исходников: `Ios/BarkCloud/Proto/grpc-swift-proto-generator-config.json` (конфиг плагина-генератора) и `Ios/BarkCloud/sync_proto.sh` (Run-Script: синхронизация `.proto` из `Shared/BarkCloud.Proto`).

## Конфигурация (project.pbxproj)

| Параметр | Значение |
|---|---|
| `PRODUCT_BUNDLE_IDENTIFIER` | `com.barkfluff.BarkCloud` |
| `IPHONEOS_DEPLOYMENT_TARGET` | `18.0` |
| `SUPPORTED_PLATFORMS` | `iphoneos iphonesimulator` |
| `SDKROOT` | `iphoneos` |
| `TARGETED_DEVICE_FAMILY` | `1,2` (iPhone + iPad) |
| `SWIFT_VERSION` | `5.0` |
| `SWIFT_DEFAULT_ACTOR_ISOLATION` | `MainActor` |
| `SWIFT_APPROACHABLE_CONCURRENCY` | `YES` |
| `LOCALIZATION_PREFERS_STRING_CATALOGS` | `YES` |
| `ENABLE_APP_SANDBOX` | `YES` |
| `knownRegions` | `en, ru, de, Base` |

## Зависимости (SPM, подключены)

| Пакет | URL |
|---|---|
| grpc-swift-2 | `https://github.com/grpc/grpc-swift-2` |
| grpc-swift-nio-transport | `https://github.com/grpc/grpc-swift-nio-transport` |
| grpc-swift-protobuf | `https://github.com/grpc/grpc-swift-protobuf` |
| swift-protobuf | `https://github.com/apple/swift-protobuf` |

Прописаны в `project.pbxproj` (XCRemoteSwiftPackageReference). Keychain — нативный `Security` framework (без сторонних зависимостей).

## Настройка проекта (выполнено)

См. историю в `Docs/IOS_SETUP.md`. Уже применено к проекту:
1. 4 SPM-пакета добавлены.
2. Build-tool plugin `GRPCProtobufGenerator` подключён (стабы в `Generated/Proto/`).
3. Run-Script build phase синхронизирует `.proto` из `Shared/BarkCloud.Proto` (`sync_proto.sh`).
4. ATS для self-signed TLS задан через build settings (отдельного `Info.plist` нет).

## Соответствие Android-клиенту

| Android | iOS |
|---|---|
| `BarkCloudApplication` (service locator) | `AppEnvironment` через `.environment(_:)` |
| `GlobalParam` (EncryptedSharedPreferences) | `SessionStore` (Keychain) |
| `ViewModel + StateFlow<UiState>` | `@Observable` класс + value-type UiState |
| `OkHttp + grpc-okhttp + grpc-kotlin` | `GRPCCore + GRPCNIOTransportHTTP2 + GRPCProtobuf` |
| `Coil + VideoFrameDecoder` | `QLThumbnailGenerator + NSCache` (PR 6) |
| `Material3 Theme` | SwiftUI `.tint` + asset-catalog accent |
| `androidx.navigation.compose NavHost` | SwiftUI `NavigationStack` (на таб) + `TabView` (5 табов: Галерея/Файлы/Альбомы/Корзина/Настройки, default = Альбомы) |
| `FileProvider + ACTION_SEND` | `UIActivityViewController` через `UIViewControllerRepresentable` (PR 5) |
| `BuildConfig.IDENTITY_API_ADDRESS = https://cloud.barkfluff.com:7020` | `ServerConfig` (App Group UserDefaults) → `GrpcEndpoint`: per-service host+port, useTLS, allowSelfSigned — задаёт пользователь на первом запуске (см. [[#Server Setup]]); дефолт `ServerConfig.production` = боевые адреса |

## Серверная интеграция (реализовано)

Облачный функционал подключён к боевому бэкенду (см. [[api/files-client-guide]], [[api/users-client-guide]]):

- **Авто-обновление токена** (`Networking/GrpcManager.swift`, образец — iOS-клиент Barkfluff,
  `BFNetworking/Auth/{AuthInterceptor,TokenRefreshCoordinator}`). Раньше при истечении access-токена
  авторизация «слетала» — обновления не было. Теперь `AuthInterceptor` перед каждым запросом запрашивает
  токен у `GrpcManager.accessToken(forMethod:)`, который **проактивно** обновляет его, если до истечения
  осталось < 60 c (или токена нет): читает снимок `SessionStore.snapshot()`, при необходимости вызывает
  `Identity.CreateToken(refresh_token)` и сохраняет новый access-токен (`saveRefreshedAccessToken`).
  Обновление **сериализовано** полем `refreshTask` (актор-изоляция `GrpcManager`): при пачке параллельных
  запросов `CreateToken` вызывается один раз, остальные ждут результат. **Публичным (без авторизации) RPC
  Identity — `Auth` (логин) и `CreateToken` (refresh) — токен НЕ прикрепляется** (`unauthenticatedMethods`):
  иначе просроченный/чужой токен из Keychain ловит auth-middleware сервера и отвечает HTTP 401 ещё до метода
  (логин падает «non-200 HTTP Status Code (401)»); для `CreateToken` это заодно исключает рекурсию refresh.
  Тот же путь (`grpc.validAccessToken()`)
  использует HTTP-аплоад в `FileTransferService`. Если refresh-токен истёк локально или сервер ответил
  `UNAUTHENTICATED` — `SessionStore.invalidate()` чистит токены и поднимает наблюдаемый `sessionExpired`,
  и `RootView` уводит на экран логина. Успешная авторизация сбрасывает флаг (`AuthRepository.persist`).
- **Настройки** (`Features/Settings/`) — таб «Настройки» вместо заглушки: профиль (`GetUser`),
  аватар через PhotosPicker (`USER_AVATAR` → upload → `SetProfilePicture`; удаление — `SetProfilePicture("")`).
  Отображение аватара устойчиво: `FallbackRemoteImage` пробует по очереди `profile_picture_preview`,
  затем `profile_picture` (с проверкой HTTP-статуса), а каждый URL прогоняется через
  `GrpcEndpoint.normalizedFileDownloadURL` — сохранённая в БД ссылка могла быть сгенерирована при
  прежней конфигурации `ExternalEndpoint:Host`, поэтому хост/порт пересобираются на актуальный
  `cloud.barkfluff.com:7025/web/download/{id}`.
  редактирование имени/юзернейма/bio (`ChangeName`/`ChangeUsername`+`CheckExistUsername`/`ChangeBio`),
  приватность (`Get/UpdatePrivacySettings`), устройства (`GetDevices`/`GetCurrentDevice`/`RenameDevice`/`DeleteDevice`),
  хранилище (`GetUserStorageInfo`), выход и удаление аккаунта (`DeleteAccount`).
  Sign-out проброшен `RootView → MainScreen → SettingsScreen` через `onSignOut`.
  **Выход** централизован в `AppEnvironment.signOut()`: серверный отзыв сессии `Identity.Logout`
  (best-effort, до очистки токенов) → `resetLocalState()`. **`resetLocalState()` — полный сброс до
  «свежей установки»** (объём выбран как «полный сброс устройства»): `SessionStore.clearSession()`
  (Keychain-токены) + `GrpcManager.shutdown()` (соединения) + `BackgroundUploadCoordinator.cancelAll()`
  (отмена live-задач фоновой сессии) + `UploadQueueStore.deleteAll()` (очередь загрузок) +
  `BackupManager.setAutoUpload(false)` + `RemoteImageCache.clear()` + `InsecureHTTP.clearCaches()`
  (URL-кэш/куки) + `FileCacheService.clearAll()` + `AssetHashStore.clearAll()` + `FileCacheSettings.reset()`
  + `AppLockSettings.disable()` (стирает PIN/соль из Keychain) + `VaultStore.removeAll()` (локальный «сейф»)
  + `ServerConfigStore.reset()` (`ServerConfig.clear()` → `isConfigured=false`). После сброса `RootView`
  уходит **на `ServerSetupScreen`** (адреса сервера стёрты), а не на Login. Удаление аккаунта
  использует тот же `resetLocalState()` без серверного `Logout` (аккаунт уже удалён). На время операции —
  блокирующий оверлей (`isProcessing`), защищающий от повторных нажатий.
  **Пункт «Приложение»** (`AppSettingsScreen`) — переключатель «Блокировка входа» (Face ID + PIN),
  см. [[#App Lock]]: включение требует подтверждения биометрией и задания PIN через [[SetPinSheet]],
  выключение — биометрии.
- **Галерея** (`Features/Gallery/`) — таб №1, локальная медиатека устройства через **PhotoKit**
  (`PHAsset`, разрешение `NSPhotoLibraryUsageDescription` в build-settings pbxproj). Сетка фото+видео
  (`PHCachingImageManager`), тап → полноэкранный просмотр **со свайпом влево/вправо** между файлами
  (`MediaPagerScreen`, см. [[#Свайп-просмотрщик]]). **Фото** показываются через **QuickLook**, поэтому
  доступны нативные фишки iOS: выделение объекта на фото (Visual Look Up / subject lifting), Live Text,
  зум, шаринг. Для этого `DeviceMediaImageLoader.exportPhotoToTempFile(for:)` потоково выгружает оригинал
  ассета во временный файл (`PHAssetResourceManager.writeData`, приоритет ресурсов как при загрузке —
  имя сохраняет расширение, чтобы QuickLook определил тип). **Видео** тоже идёт в QuickLook через прямой
  URL файла медиатеки (`DeviceMediaImageLoader.videoFileURL` → `requestAVAsset`/`AVURLAsset.url`, без
  копии на диск; ранее — `VideoPlayer`, теперь единый просмотрщик ради свайпа).
  Режим выбора → загрузка выбранных в облако (`DeviceAssetResource.originalData` → `CloudRepository.uploadFile`).
  **Медиа привязывается к авто-папке «Недавно загруженные»** (`CloudRepository.ensureRecentUploadsFolder()`:
  листает корень, ищет папку с именем `recentUploadsFolderName`="Недавно загруженные", создаёт при отсутствии →
  `uploadFile(toDirectory:)`; best-effort — без папки файл всё равно в галерее по uploader'у). Повторяет
  `ensureRecentFolder()` веб-клиента (`ClientApp/.../PhotosPage.tsx`), чтобы у медиа была запись каталога
  (работают корзина/переименование) и оно попадало в эту папку. То же делает вкладка Медиа Фото/Видео.
  Чтение оригинала и потоковый SHA256 вынесены в общий `DeviceAssetResource` (используют и Галерея, и
  кастомный пикер загрузки), а машинерия «уже в облаке» — в `CloudPresenceTracker` (`@Observable`).
  **Баг тапа по соседней строке в сетках** (`Features/Shared/MediaThumb.swift` → `SquareThumbClip`):
  у `RemoteImage(contentMode:.fill)`/`scaledToFill` фрейм картинки переполняет квадрат ячейки по большей
  стороне; `.clipped()`/`.clipShape` прячут переполнение лишь визуально, но НЕ обрезают хит-тест — и
  невидимый «хвост» картинки нижней строки перехватывает тап по текущей ячейке (`.contentShape(Rectangle())`
  на родителе это не лечит). `SquareThumbClip` задаёт содержимому явный квадратный фрейм через
  `GeometryReader` + `.clipped()`, поэтому область нажатий совпадает с ячейкой. Применён во всех сеточных
  ячейках: `MediaThumb` (фото/видео + альбомы), `DeviceMediaThumb` (галерея устройства), `AlbumCardView`
  (обложки альбомов). **Иконка облака**: лениво (по появлению ячейки)
  считается потоковый SHA256 оригинала и пакетно (дебаунс 400 мс, чанки по 500) проверяется через
  `FilesApi.CheckFileHashes` — если файл с таким хешем уже в облаке, рисуется `checkmark.icloud.fill`.
  Хеш считается тем же ресурсом, что и при загрузке, поэтому совпадает с серверным. Эта логика
  инкапсулирована в `CloudPresenceTracker` и переиспользуется кастомным пикером загрузки.
  **Дубликаты разрешены** (`DeviceAssetPickerScreen`): пикер больше не блокирует выбор уже-загруженных
  (бейдж «уже в облаке» остаётся), а при подтверждении, если среди выбранных есть дубликаты, показывает
  `confirmationDialog` «часть файлов уже в облаке — загрузить ещё раз?» (Загрузить всё / Только новые /
  Отмена). Серверный дедуп снят (Plan 01) → ручная загрузка всегда создаёт копию; автозагрузка
  (`BackupManager.classify`) по-прежнему пропускает уже-загруженное по серверному хешу (`CheckFileHashes`).
  **Авто-распределение по типу** (`route_by_media_kind`): передние загрузки через `CloudRepository.uploadFile`
  (`GalleryViewModel.uploadSelected`/`ensureCloudFileID`, `MediaGridViewModel.uploadAssets`) грузят **без явной
  папки** с `routeByMediaKind: true` — сервер сам кладёт в системные «Фото»/«Видео»/«Другие документы»
  (`uploadFile`/`attachFile` принимают флаг и шлют `AttachFileRequest.route_by_media_kind`). Фоновые загрузки
  (`BackupManager` автозагрузка, `ShareInboxUploader`/Share Extension) пока используют `ensureRecentUploadsFolder`:
  их привязка отложена через персистентный `UploadJob` (+ отдельный таргет Share Extension), поэтому проброс
  флага туда не сделан, а сам хелпер `ensureRecentUploadsFolder`/`recentUploadsFolderName` оставлен. Сборка на
  macOS не выполнялась (хост недоступен) — правки верифицированы анализом кода.
  **Резервная копия / BarkCloud** (`Features/Gallery/Backup/`) — кнопка-облако (`icloud`) в тулбаре
  правее «Выбрать» открывает модалку `BackupSheet` (плавающая карточка с отступами: `fullScreenCover` +
  `.presentationBackground(.clear)` + затемнение + `.padding(20)` + `.regularMaterial`; заголовок
  модалки = строка `backup_title` = "BarkCloud", раньше "Резервная копия"). На самой кнопке тулбара —
  **кольцевой прогресс** общей автозагрузки: показывается, пока `autoUploadEnabled` и `remainingCount > 0`
  (когда очередь опустела — кольцо убирается, чтобы 100%-дуга не «висела» до следующего открытия);
  вокруг `Image("icloud")` в `ZStack` рисуется `Circle().stroke` (фон, `accent.opacity(0.25)`)
  + `Circle().trim(0,progress).stroke(accent, .round)` с `rotationEffect(-90°)`, диаметр 28pt,
  `.animation(.easeOut(0.4), value: progress)`; `progress = uploadDone / (uploadDone + uploadFailed + remainingCount)`;
  пока кольцо показано, сама иконка облака уменьшается (`.font(.system(size:15))`), чтобы дуга на неё не налезала.
  Внутри модалки: **hero-донат хранилища** (`StorageDonut`, 104pt — `Circle().stroke` фоном + `Circle().trim(0,fraction)` дугой,
  по центру крупный `%`) на фоне `accent.opacity(0.10)` + текстовый блок сбоку (используется `FileTransferService.storageInfo()`);
  тогл автозагрузки фото/видео в карточке с фоном `onSurface.opacity(0.05)` и круглой иконкой `icloud.and.arrow.up.fill`
  в `accent.opacity(0.18)` кружке; при включении — статус скана, прогресс-бар загружено/осталось, статус «Всё загружено»
  с зелёной галкой и ряд превью очереди (текущий + 3 следующих = 4 в ряду; `HStack` flexible aspect-ratio квадратами
  с `padding(.horizontal, 8)`, чтобы крайние не клипились карточкой; перестройка анимируется через
  `.animation(.interpolatingSpring, value: queuePreview.map(\.localIdentifier))` +
  `.transition(.asymmetric(insertion:.move(.trailing), removal:.move(.leading)))` — текущий уезжает
  влево «по курве», следующие сдвигаются на его место, новый въезжает справа, как конвейер);
  отдельная карточка освобождения места — оранжевая иконка `trash.fill`, оценка освобождаемого размера и **filled-capsule** кнопка-CTA
  (`accent` фон, белый текст, дизейблится без кандидатов; `sparkles` иконка слева). Ядро — **`BackupManager`** (`@MainActor @Observable`, живёт в
  `AppEnvironment`, а не во вью — Task'и (`scanTask`/`uploadTask`) хранятся внутри менеджера, чтобы
  ре-рендер/закрытие модалки их не отменял; тот же урок, что в pull-to-refresh). Скан медиатеки —
  **прогрессивный**, последовательно (конкурентность 1, чтобы видео не раздували память): для каждого
  ассета `DeviceAssetResource.cachedSHA256` → пачками по 100 в `CloudRepository.checkFileHashes`;
  «уже в облаке» → в `reclaimable` (+`DeviceAssetResource.originalByteSize`, новый KVC-хелпер по
  приватному `fileSize`), иначе → в очередь `pendingUpload`. **Кеш хешей** (`Data/Cache/AssetHashStore.swift`):
  отдельная SwiftData-БД `BarkCloudAssetHashes.sqlite` (singleton `AssetHashStore.shared`, in-memory fallback)
  хранит `localIdentifier → SHA256` оригинала с инвалидацией по `modificationDate` (есть и точечный
  `remove(localIds:)` — зовётся после освобождения места). `cachedSHA256` сначала
  смотрит в неё и считает тяжёлый потоковый хеш лишь раз — переиспользуется и `BackupManager` (скан не
  пере-хеширует всю медиатеку при каждом холодном старте), и `CloudPresenceTracker` (бейджи «уже в облаке»
  мгновенны после перезапуска). Очищается в `AppEnvironment.resetLocalState()` при выходе. **Автозагрузка** — только на переднем
  плане (без BGTaskScheduler/фоновой URLSession): `uploadLoop` берёт следующий ассет → `originalData`
  → `CloudRepository.uploadFile(toDirectory: ensureRecentUploadsFolder())`; тогл персистится в
  **`AutoUploadSettings`** (обёртка над `UserDefaults`, ключ `BarkCloud.autoUpload.enabled`), при старте
  приложения `BackupManager.resumeIfEnabled()` (из `AppEnvironment.init`) докачивает остаток. **Новые
  фото/видео ловит свой `PHPhotoLibraryChangeObserver`** (`BackupPhotoLibraryObserver` → `refreshScanForNewAssets`)
  — автозагрузка стартует сразу, без перезапуска и смены вкладки (раньше скан дёргался только на
  старте/возврате в foreground/смене таба, и новое фото на открытом таб-Галереи не грузилось).
  **Очистка осиротевших jobs** (`attachAndResubmitOrphans` на старте и каждом возврате в foreground):
  job без живого URLSession-task **удаляется** (не перезаливается — `uploadURL` одноразовый), но только
  если старше 60с — свежие jobs текущей сессии не трогаем (иначе гонка с submit «съедала» новые загрузки).
  **«Освободить место»** — `PHPhotoLibrary.shared().performChanges { PHAssetChangeRequest.deleteAssets }`
  (iOS сам показывает системное подтверждение; отмена → throw, без эффекта); при успехе показывается
  `SpaceFreedView` — оверлей с пиксель-лисой `BarkMascot` + радиальный «glow» под ним (оранжевый
  `RadialGradient` 180pt) + искры (`Canvas`/`TimelineView`) и count-up освобождённых байт; subtitle
  деловой, без наигранного «спасибо» (`backup_freed_thanks` = «Не забывайте освобождать место»);
  тонкая `onSurface.opacity(0.06)` обводка по rounded-corner; авто-скрытие через 2.4 с (или тап). После удаления: чистим `AssetHashStore.remove(localIds:)` (иначе кеш
  держал бы мёртвые `localIdentifier`), а сетка в табе Галерея обновляется сама через
  `PHPhotoLibraryChangeObserver` в `GalleryViewModel` (`registerLibraryObserverIfNeeded` →
  `handleLibraryChange` пересобирает `assets` из `changeDetails.fetchResultAfterChanges`,
  чистит `selection`) — без него удалённые фото оставались бы превью-«призраками», падающими при открытии. **Стиль** (более «дорогой» полиш): hero-донат с акцентным фоном, секции на нейтральных карточках с
  `RoundedRectangle(cornerRadius: 20)`, ведущие SF-иконки в круглых акцентных «шайбах»
  (`icloud.and.arrow.up.fill` для автозагрузки в accent-кружке, `trash.fill` для очистки в оранжевом),
  CTA освобождения — filled-capsule (accent → белый), управление автозагрузкой — нативный iOS-тогл,
  заголовок шапки — `icloud.fill` в accent-шайбе + крупный `BarkCloud`, крестик — кружок-кнопка.
- **Альбомы** (`Features/Media/`, таб №3, по умолчанию) — `CloudMediaScreen` с переключателем
  **Фото / Видео / Альбомы**. Фото/Видео: `CloudApi.ListUserMedia(kind)` с cursor-пагинацией и догрузкой,
  превью через `RemoteImage`, тап → полноэкранный QuickLook (`GetTempDownloadUrl` → download)
  **со свайпом влево/вправо** ([[#Свайп-просмотрщик]]),
  загрузка через кастомный `DeviceAssetPickerScreen` (сетка медиатеки устройства как в Галерее, бейджи
  «уже в облаке» из `CloudPresenceTracker`; в Фото/Видео уже загруженные нельзя выбрать повторно)
  → `DeviceAssetResource.originalData` → `GetUploadUrl(CLOUD_FILE)` → HTTP. **Мультивыбор** в Фото/Видео:
  кнопка «Выбрать» рядом с «+» включает режим выбора (галочки на `MediaThumb`); нижняя панель кнопок без фона
  появляется с анимацией (`safeAreaInset` + `transition(.move(edge:.bottom))`) — «Удалить» (подтверждение —
  `.popover(arrowEdge:.bottom)` над самой кнопкой, `presentationCompactAdaptation(.popover)`; последовательно
  `CloudApi.DeleteUserMedia(file_id)`, на время операции кнопки заменяются прогресс-баром done/total) и
  «В альбом» (`AlbumPickerSheet`: список альбомов + первым пунктом «Создать новый альбом» →
  `CreateAlbum("Новый альбом"+5 случайных символов)` + `AddItemsToAlbum`). **`DeleteUserMedia`** (новый RPC,
  бэкенд `Backend/BarkCloud.Files/Features/Cloud/DeleteUserMedia/`): живые `CloudFileEntries` владельца → в
  корзину (восстановимо); если записей нет (медиа загружено без привязки к папке) — `RemoveUploaderFromFile`
  (жёсткое удаление из галереи, освобождает квоту). Решает проблему: медиа из таба грузится без записи
  каталога, поэтому `DeleteFileEntry`/`entry_ids` для него не работали.
  Альбомы (`AlbumApi`, `kind=nil` — без
  фильтра): карточки (`ListAlbums`), открытие (`ListAlbumItems`), создание, добавление файлов тем же
  пикером (в альбом разрешено добавлять и уже загруженное — `uploadFile` дедуплицирует по хешу),
  смена обложки, удаление элементов/альбома. Во всех трёх под-вкладках — pull-to-refresh
  (`.barkRefreshable` → `reload()`), работает и на пустом состоянии. **Важно (как в Корзине/Облаке):**
  у `AlbumsViewModel.reload(showSpinner:)` потягивание передаёт `showSpinner: false` — иначе ветка
  `if isLoading { ProgressView() }` в `AlbumsGridScreen` свернула бы `ScrollView` (носитель жеста
  pull-to-refresh и overlay-лисы) в полноэкранный `ProgressView` прямо во время обновления — контент
  и лиса исчезали бы на месте. Спиннер первого показа даёт дефолт `isLoading=true`,
  программный `reload()` после `create()` идёт со спиннером. Сетки Фото/Видео (`MediaGridViewModel`)
  этим не страдают — там нет ветки переключения вида по `isLoading`.
- **Корзина** (`Features/Trash/`, таб №4) — `CloudApi.ListTrash` с cursor-пагинацией, превью/иконка
  по типу, дата удаления и срок очистки; свайп — `RestoreFromTrash` / `DeleteFromTrash` (**только
  иконки**, без подписей; «удалить навсегда» оптимистично через [[#PendingDelete]] — элемент сразу
  убирается, внизу snackbar с обратным отсчётом 5 с и кнопкой «Отменить»); в тулбаре —
  `EmptyTrash` с подтверждением и блокирующим оверлеем. Pull-to-refresh (`.barkRefreshable` → `reload()`),
  работает и на пустом состоянии (пустой экран обёрнут в `ScrollView`). **Важно:** `reload()` НЕ поднимает
  `isLoading` — иначе при потягивании экран свернул бы `List` (носитель жеста pull-to-refresh и overlay-лисы)
  в полноэкранный `ProgressView` прямо во время обновления, и контент с лисой исчезали бы
  (спиннер первого показа даёт дефолт `isLoading=true`; индикатор потягивания — фирменная лиса в overlay).
- **Файлы** (`Features/Files/`, таб №2) — секции: «На устройстве» (`LocalBrowserScreen`),
  «Облачное хранилище» (карточка-вход в `CloudBrowserScreen`: навигация по папкам
  `ListDirectoryDetailed`, хлебные крошки `GetPath`, CRUD папок/записей, перемещение через
  `CloudMovePicker`, загрузка фото/видео (PhotosPicker) и документов (`.fileImporter`), открытие/скачивание
  в QuickLook, **swipe-actions только иконками** (`trash`/`folder`/`pencil`, без подписей), удаление
  файла/папки — оптимистичное через [[#PendingDelete]] (внизу snackbar 5 с с «Отменить»),
  pull-to-refresh `.barkRefreshable` → `reload(showSpinner: false)` — флаг отключает подъём
  `isLoading` при потягивании, чтобы не свернуть `List` (с жестом и лисой) в полноэкранный `ProgressView`; программные обновления
  после CRUD зовут `reload()` со спиннером (и на пустой папке через `ScrollView`))
  и «Общий доступ» → `SharedHubScreen`.
  **Мультивыбор в облачном браузере** (`CloudBrowserViewModel`/`CloudBrowserScreen`): кнопка
  «Выбрать» в тулбаре включает режим выбора (галочки `checkmark.circle`, навигация по папкам и
  открытие файлов отключены, можно выделять и **файлы, и папки** — `selectedFiles`/`selectedDirs`).
  Нижняя панель (`safeAreaInset`, фон `.bar`): «Переместить» (`CloudMovePicker` → `moveSelected`)
  и «Удалить» (подтверждение поповером → `deleteSelected`, батч последовательно с прогрессом
  done/total, без undo — зеркалит `MediaGridViewModel`).
- **Общий доступ** (`Features/Shared/SharedHubScreen.swift`) — хаб с **тремя** сегментами:
  - **Мои публичные** (`MySharesListView`/`MySharesViewModel`) — постоянные публичные ссылки
    (`CloudApi.ListMyShares`, копировать/отозвать). См. [[share-links-client-guide]].
  - **Я поделился** (`MyOutgoingSharesListView`/`MyOutgoingSharesViewModel`) — файлы, которыми я
    поделился с конкретными пользователями (приватные гранты), и с кем. Источник —
    `CloudApi.ListMyOutgoingSharesAll` (плоский список грантов с `UploadFileInfo`, курсор-пагинация);
    VM **группирует по файлу** (`SharedByMeGroup` — порядок по первому появлению, raw отсортирован от
    свежих) и резолвит получателей через `UserRepository.getUser`. Карточка: превью + имя файла +
    чипсы получателей (`FlowLayout`) с крестиком — отзыв гранта `revokeUserShare` (оптимистично).
    Зеркалит веб-таб `SharedPage.tsx` (`/api/shared/i-shared`).
  - **Мне доступны** (`SharedWithMeListView`/`SharedWithMeViewModel`) — входящие гранты
    (`ListSharedWithMe`, скачивание через `GetSharedFileDownloadUrl` → `UIDocumentPicker`).
  VM каждого таба создаётся/грузится лениво при первом переключении. Выдача гранта на файл —
  `ShareWithUserSheet` (поиск пользователя), управление одним файлом — `OutgoingSharesSheet`;
  обе вызываются из контекстного меню (`CloudBrowserScreen`/сетки). Бэкенд: новый RPC
  `ListMyOutgoingSharesAll` + фича `Features/Cloud/ListMyOutgoingSharesAll` в сервисе Files.
- **Контекстное меню по удержанию на сетках** (`Features/Shared/ShakeContextMenu.swift`) — кастомный
  `ViewModifier` `.shakeContextMenu(isActive:menu:)` вместо нативного `.contextMenu`: по
  `.onLongPressGesture(minimumDuration:0.4)` ячейка увеличивается (`scaleEffect 1.09`), «трясётся»
  (`rotationEffect ±1.5°`, `repeatForever` через `.animation(_:value:)`) и даёт слабую тактильную
  отдачу (`Haptics.light()` = `UIImpactFeedbackGenerator(.soft)`; заметна только на устройстве), затем
  открывается `.confirmationDialog` с действиями над файлом. `isActive=false` (режим мультивыбора)
  отключает жест. Применён на трёх сетках: Фото/Видео (`MediaGridScreen`), содержимое альбома
  (`AlbumDetailScreen` — заменил прежний `.contextMenu` со «Сделать обложкой»/«Убрать из альбома»,
  оба сохранены), Галерея устройства (`GalleryScreen`). Пункты: **Свойства**, **Копировать ссылку**
  (`FileTransferService.tempDownloadURLs` → `UIPasteboard.general.url`), **Сделать публичной**
  (`CloudApi.CreateShare` → клиент сам собирает `{GrpcEndpoint.webHost}/s/{token}` через
  `publicShareURL(token:)`, см. [[share-links-client-guide]] — URL ведёт на веб-UI :443, бэкенд готовый
  URL не отдаёт; см. ограничение revoke в гайде), **Добавить в альбом** (`AlbumPickerSheet` на один файл),
  **Удалить** (галерея/альбом → `DeleteUserMedia` в корзину **оптимистично через [[#PendingDelete]]** —
  файл сразу пропадает из сетки, внизу snackbar 5 с с «Отменить»; устройство → «Удалить с устройства»
  `PHAssetChangeRequest.deleteAssets`). **Экран свойств** (`Features/Shared/FilePropertiesSheet.swift`,
  enum-вход `.cloud(MediaAsset)`/`.device(PHAsset)`) — имя/тип/размер/разрешение/даты/ID/устройство загрузки
  (как веб-модалка, где есть данные); `MediaAsset` расширен полями `imageWidth/imageHeight/uploadedAt/etag/uploadDeviceName` из `UploadFileInfo`.
  Для `.cloud` дополнительно через `.task` асинхронно подгружает расширенные метаданные блоба
  (`CloudRepository.getFileMetadata(fileID:)` → `CloudApi.GetFileMetadata`) и отрисовывает их секциями
  `List` поверх базовых полей: **Общее** (taken_at, creator_tool), **Камера** (make+model одной строкой,
  lens), **Параметры съёмки** (focal_length мм, f/N, выдержка `1/N с`/`X.X с`, ISO, вспышка Да/Нет),
  **Видео** (длительность mm:ss/h:mm:ss, video/audio codec uppercase, битрейт Мбит/с или кбит/с, fps),
  **Геолокация** (координаты с 6 знаками после точки, высота в м) и **Документ** (title, author, subject,
  pages). При `has_metadata=false` (легаси-блобы без бэкафилла, либо файл без EXIF/ffprobe-полей) — секции
  не показываются, базовые поля остаются.
  На Галерее устройства у `PHAsset` нет `file_id` — `GalleryViewModel.ensureCloudFileID(for:)` резолвит его
  по SHA256 (`cachedSHA256` → `CloudApi.CheckFileHash`, одиночный), а при отсутствии заливает оригинал
  (дедуп по хешу) в авто-папку «Недавно загруженные»; на время резолва — оверлей `isUploading`.

### Свайп-просмотрщик

`Features/Shared/MediaPagerScreen.swift` — полноэкранный просмотрщик с листанием
влево/вправо между файлами коллекции. Применён в **Альбомы-таб** (`MediaGridScreen`
Фото/Видео), **Галерее** устройства (`GalleryScreen`) и **содержимом альбома**
(`AlbumDetailScreen`) — заменил одиночные `RemoteFilePreviewScreen`/`DeviceMediaViewer`
(последний удалён).

- **Почему многоэлементный `QLPreviewController`, а не внешний пейджер:** внутренний
  скролл-вью QuickLook (зум) перехватывает горизонтальные жесты, поэтому обёртка в
  `TabView`/`ScrollView(.paging)` не листала бы. QL сам реализует свайп между
  элементами + нижнюю ленту превью + зум.
- **Ленивый резолв URL:** `MediaPager` (UIViewControllerRepresentable) принимает `ids`
  + `startIndex` + `resolve: (String) async -> URL?`. `Coordinator` (dataSource) на
  каждый запрос элемента резолвит его и соседей (`ensure`), для нерезолвленных отдаёт
  прозрачную 1×1 PNG-заглушку; когда URL текущего готов — `refreshCurrentPreviewItem()`.
  Стартовый элемент применяется один раз и в `make`, и в `update` (QL иногда игнорит
  ранний `currentPreviewItemIndex`).
- **Резолверы:** облако — `MediaPagerResolver.cloud(transfer:cache:)` (скачивает оригинал
  через дисковый кеш, тот же путь, что `RemoteFilePreviewScreen`); устройство —
  `GalleryScreen.deviceResolve` (фото → `exportPhotoToTempFile`, видео → `videoFileURL`).
- **Пагинация:** опциональный `loadMore: () async -> [String]` — при подходе к концу
  (`index >= ids.count - 2`) `Coordinator` догружает следующую страницу через VM
  (`loadMoreIfNeeded`), дописывает `ids` и зовёт `reloadData()` с восстановлением текущей
  позиции — листается до конца без выхода к сетке. Передан в Альбомы-таб и содержимое
  альбома; в Галерее устройства `nil` (медиатека грузится целиком, пагинации нет).
- **Ограничения:** видео играет в QuickLook **без автостарта** (у `QLPreviewController`
  нет API автозапуска; ковыряние внутренней иерархии вью отвергнуто как хрупкое/риск App
  Store). При первом открытии не-кешированного файла короткий пустой кадр, пока идёт
  докачка; неуспешный резолв оставляет прозрачную заглушку.

### PendingDelete

Общий компонент отложенного удаления для всех экранов (`Features/Shared/PendingDelete.swift` +
`PendingDeleteSnackbar.swift`) — паттерн «undo-snackbar» как в Gmail. `@MainActor @Observable`
store с одной активной записью `Pending { id, label, action, onUndo }` и `remainingSeconds`.
**Логика:**
1. View-модель сразу убирает элемент из своей коллекции (оптимистично).
2. Зовёт `pendingDelete.schedule(label:action:onUndo:)`; внизу появляется snackbar с именем файла,
   отсчётом `Удалится через N с.` и кнопкой «Отменить» (capsule на accent-фоне).
3. Через 5 секунд срабатывает таймер — выполняется `action` (реальный gRPC-запрос на сервер).
4. Если до этого пользователь ставит **другое** удаление через `schedule()` — предыдущее
   немедленно исполняется в фоне (его `action` уходит), snackbar обновляется на новый элемент
   со своим 5-секундным отсчётом. Тот же эффект даёт `await pendingDelete.flushIfAny()`,
   который во всех VM вызывается **в начале `reload()`**, чтобы pull-to-refresh не вернул
   только что удалённый элемент с сервера.
5. Кнопка «Отменить» зовёт `cancel()` — таймер останавливается, `onUndo()` восстанавливает
   элемент в коллекции по запомненному индексу (`min(index, count)` на случай вставок).
6. При ошибке от сервера в action — VM показывает обычный текстовый snackbar и зовёт
   `reload()` (элемент возвращается с сервера, состояние синхронизируется).

Применён: `CloudBrowserViewModel.deleteFile/deleteDirectory`, `TrashViewModel.deleteForever`,
`MediaGridViewModel.deleteSingle`, `AlbumDetailViewModel.deleteFromCloud` — то есть везде, где
пользователь удаляет один файл (через swipe в списке или контекстное меню на сетке). Batch-удаление
(`MediaGridViewModel.deleteSelected`) пока без undo — там свой прогресс-индикатор. Удаление
устройства (`DevicesScreen`) и удаление альбома/аккаунта — через confirmation-диалог,
тоже без undo.

**Swipe-actions: только иконки** (без подписей) на всех экранах со свайпом: `TrashScreen`
(`arrow.uturn.backward`/`trash`), `CloudBrowserScreen` (`trash`/`folder`/`pencil`),
`DevicesScreen` (`trash`/`pencil`); вместо `Label(..., systemImage:)` — голый `Image(systemName:)`
+ `.accessibilityLabel(...)` для VoiceOver.

### Синхронное удаление устройство↔облако

Удаление держит копии на устройстве (PhotoKit) и в облаке согласованными в обе стороны.

- **Индекс связей** `Data/Cache/CloudDeviceLinkStore.swift` (`@Model CloudDeviceLink`,
  `actor CloudDeviceLinkStore.shared`, отдельная БД `BarkCloudCloudDeviceLinks.sqlite`,
  in-memory fallback — образец `AssetHashStore`): `file_id ↔ localIdentifier`. Заполняется,
  когда клиент достоверно знает обе стороны: при загрузке ассета в облако
  (`GalleryViewModel.uploadSelected`/`ensureCloudFileID`, `MediaGridViewModel.uploadAssets`,
  `AlbumDetailViewModel.uploadAndAddAssets`) и при подтверждении наличия по SHA256 в
  `CloudPresenceTracker.linkFileID` (для подтверждённого-в-облаке ассета один `CheckFileHash`
  резолвит `file_id` → связь; покрывает авто-загруженные в фоне, т.к. `UploadJob` не несёт
  `localIdentifier`). Очищается в `AppEnvironment.resetLocalState()`.
- **Направление устройство → облако** (таб «Галерея», контекстное меню):
  - «Удалить с устройства» (`ctx_delete_device`) → `deleteFromDevice` — только медиатека, облако остаётся.
  - «Удалить везде» (`ctx_delete_everywhere`) → `deleteEverywhere`: `resolveCloudFileIDIfPresent`
    (SHA256 → `checkFileHash` **без заливки**, nil если в облаке нет) → сначала системное удаление
    ассета; если отменили — облако не трогаем; иначе `deleteUserMedia` (в корзину). `deleteFromDevice`
    теперь `@discardableResult -> Bool` и чистит осиротевшие `CloudDeviceLinkStore`/`AssetHashStore`.
- **Направление облако → устройство** (`Features/Shared/DeviceCopyCleaner.swift`, `@MainActor enum`):
  `deleteDeviceCopies(forCloudFileIDs:)` резолвит `localIdentifier` через индекс → `PHAsset.fetchAssets`
  → один `PHAssetChangeRequest.deleteAssets` на пачку (системный диалог) → чистит индекс и кеш хешей.
  Подключён **после успешного облачного удаления везде**: `MediaGridViewModel.deleteSingle`/`deleteSelected`,
  `AlbumDetailViewModel.deleteFromCloud`, `CloudBrowserViewModel.deleteFile`/`deleteSelected`
  (по `entry.fileID`), `TrashViewModel.deleteForever` (по `item.fileID`). Для одиночного удаления
  через [[#PendingDelete]] чистка устройства идёт **в `action`** (после отсчёта 5 с) — Undo отменяет
  и облако, и устройство, ценой отложенного системного диалога.
- **Ограничения:** системный диалог удаления из медиатеки не подавить; при Limited-доступе ассет
  может быть невидим (no-op); файл, загруженный не с этого телефона и ни разу не сканированный в
  Галерее, не имеет связи в индексе — копия на устройстве не удалится (best-effort выбранного
  iOS-only подхода без серверного SHA256 в `UploadFileInfo`).

### Server Setup

BarkCloud — self-hosted, поэтому адреса микросервисов вводит пользователь, а не хардкод.

- **Модель** `ServerConfig` (`Networking/GrpcManager.swift`, `Sendable`): per-service `host`+`port`
  (Identity/Users/Files), `useTLS`, `allowSelfSigned`. Хранится в **App Group UserDefaults**
  (`UploadConstants.appGroupID` = `group.com.barkfluff.BarkCloud`) — чтобы конфиг видел и Share
  Extension (отдельный процесс, тоже создаёт `GrpcManager`). Ключи `BarkCloud.server.*` + флаг
  `configured`. `ServerConfig.current` отдаёт сохранённое или `production`-дефолты (боевые адреса);
  `ServerConfig.isConfigured` — был ли первый ввод; `persist()` пишет ключи и поднимает `configured`.
- **`GrpcEndpoint`** теперь статические computed-аксессоры над `ServerConfig.current`
  (`identityHost/identityPort/...`, `useTLS`, `allowSelfSigned`, `scheme`, `filesWebBase`, `webHost`).
  `GrpcManager.client(host:port:)` кэширует `GRPCClient` по ключу `"host:port"` (раньше — по порту,
  один общий хост). `webHost`/`filesWebBase` выводятся из **хоста Files** (в этой архитектуре веб-UI
  и файловая раздача на одном хосте). TLS-делегаты `InsecureURLSession`/`BackgroundUploadCoordinator`
  доверяют self-signed только при `allowSelfSigned` и `host == GrpcEndpoint.filesHost`.
- **`ServerConfigStore`** (`App/ServerConfigStore.swift`, `@MainActor @Observable`) — UI-обёртка:
  `config`, `isConfigured`, `save(_:)`. Живёт в `AppEnvironment`. `RootView` показывает
  `ServerSetupScreen`, пока `!serverConfig.isConfigured` (перед веткой Login/Main).
- **`ServerSetupScreen`** (`Features/ServerSetup/`) — форма в стиле `LoginScreen` (крупный заголовок
  `displaySmall`, секции по сервисам с host+port, тумблеры TLS/самоподписанный, prominent-кнопка
  «Продолжить»). Поля предзаполнены текущей конфигурацией (на первом запуске — `production`-дефолты).
  Валидация: host непустой (схема/слеши срезаются), port 1…65535. Сохранение → `serverConfig.save` +
  `grpcManager.shutdown()` (сброс кэшированных соединений к старому адресу). Параметры `onCancel`
  (ненил → показывает «Закрыть», режим листа) и `onComplete`. Доступен повторно с экрана логина
  (ссылка «Настройки сервера» → лист) — чтобы можно было исправить неверный адрес, не «закирпичив» вход.

### App Lock

Защита запуска приложения биометрией с резервным PIN. Включается из Настройки → Приложение
(`AppSettingsScreen`). Хранение и логика — три файла:

- `Data/Cache/AppLockSettings.swift` — `@MainActor @Observable` модель. Флаг `isEnabled` и счётчик
  `failedAttempts` живут в `UserDefaults` (переживают kill процесса). Соль (16 байт случайных,
  `SecRandomCopyBytes`) и хеш PIN — в **Keychain** (`service = com.barkfluff.BarkCloud.appLock`,
  `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`). Хеширование — **PBKDF2-HMAC-SHA256, 100 000
  итераций**, 32-байтовый derived key (`CommonCrypto.CCKeyDerivationPBKDF`); сравнение
  constant-time. На 3-й неверной попытке `registerFailure()` возвращает `true` (wipe).
- `Features/AppLock/AppLockManager.swift` — координатор. Держит `isUnlocked` (state на сессию)
  и `backgroundedAt`; в `handleScenePhase(_:)` запоминает время ухода в фон и при возврате в
  `.active` снова закрывает экран, если прошло **>30 секунд** (grace для шеринга/файлпикеров).
  `unlockWithBiometric(reason:)` дёргает [[BiometricGate]] (`deviceOwnerAuthentication` =
  Face ID/Touch ID + код-пароль устройства как fallback). `verifyPin(_:)` → `.success`,
  `.wrong(remaining)`, `.wiped`. Wipe делегирует обратно в `AppEnvironment` через колбэк
  `onWipe`: `resetLocalState()` (полный сброс — токены, кеши, очередь загрузок, «сейф», PIN, адреса
  сервера; см. выше) + сам `settings.disable()`. После wipe `RootView` уходит на `ServerSetupScreen`.
- UI (`Features/AppLock/`): `AppLockScreen` (полноэкранный — между Login и Main),
  `PinKeypad`/`PinDots`/`PinEntryView` (кастомная цифровая клавиатура 3×4 — без системной IME,
  6 точек-индикаторов), `SetPinSheet` (двухшаговый мастер: ввести → подтвердить, при
  несовпадении — сбрасывается на шаг 1 с ошибкой).

Интеграция в каркас приложения: `AppEnvironment` создаёт `AppLockSettings` + `AppLockManager`
и подключает `onWipe`. `RootView` между ветками `MainScreen`/`LoginScreen` вставляет
`AppLockScreen`, когда `env.appLock.shouldShowLock` (`isEnabled && !isUnlocked`). `BarkCloudApp`
в `onChange(of: scenePhase)` зовёт `appLock.handleScenePhase(phase)` — этим и реализована
30-секундная задержка между сворачиваниями.

### Локализация

Три языка интерфейса: **ru** (sourceLanguage), **en**, **de**. Все строки в
`Resources/Localizable.xcstrings` переведены на все три (≈385 ключей; 4 чисто
символьных ключа — `""`, `..`, `@%@`, `%lld` — без переводов, рендерятся как есть).
`knownRegions` в pbxproj = `en, ru, de, Base`, `developmentRegion = en` (фолбэк).

- **Подхват от системы** — по умолчанию: при `Язык = Системный` iOS сам выбирает
  локаль устройства (через `Locale.autoupdatingCurrent`), отдельного кода не нужно.
- **Выбор в настройках (live, без перезапуска)** — Настройки → Приложение
  (`AppSettingsScreen`), новая `Section` «Язык»: 4 строки (`AppLanguage.allCases`),
  в начале строки — эмодзи-флаг (`AppLanguage.flag`: 🌐 системный, 🇷🇺/🇬🇧/🇩🇪),
  тап → `env.language.setLanguage(_:)`, текущий помечен `checkmark`.
- **Компоненты**:
  - `Data/Cache/LanguageSettings.swift` — `enum AppLanguage { system, ru, en, de }`
    (`localeIdentifier`, `displayNameKey`) + UserDefaults-обёртка (ключ
    `BarkCloud.app.language`, образец — `AutoUploadSettings`).
  - `App/Bundle+Language.swift` — подмена класса `Bundle.main` на `LocalizedBundle`
    (`object_setClass` + associated object с `.lproj`-бандлом), переопределяет
    `localizedString(forKey:value:table:)`. **Зачем:** SwiftUI `Text("key")` реагирует
    на `.environment(\.locale,…)`, но программные `String(localized:)`/`NSLocalizedString`
    environment не видят — их перенаправляет подмена бандла. `.system` → подмена снята.
  - `Features/Settings/LanguageManager.swift` — `@MainActor @Observable` (живёт в
    `AppEnvironment`): `selected`, `locale`, `setLanguage(_:)`, `reset()`; в `init`
    применяет язык к бандлу. Образец — `AppLockManager`.
- **Внедрение**: `AppEnvironment` создаёт `languageSettings`/`language` и зовёт
  `language.reset()` в `resetLocalState()` (паритет «свежей установки» → системный язык).
  `BarkCloudApp` вешает `.environment(\.locale, env.language.locale)` на `RootView` —
  корневой источник локали для всех `Text`/форматтеров (перерисовка на месте, навигация
  не сбрасывается).

**Важно для превью/скачивания**: файловый сервис на `:7025` с self-signed TLS — превью и оригиналы
грузятся через `InsecureHTTP.session` (`AsyncImage` их бы отверг), поэтому в сетках используется
`RemoteImage`, а не `AsyncImage`. Загрузка байтов — `multipart/form-data`, поле формы `file`,
`fileId` берётся из ответа (учёт дедупликации).

**Стабы**: `sync_proto.sh` регенерирует Swift-стабы из `Shared/BarkCloud.Proto` на каждой сборке
(нужны `protoc`, `protoc-gen-swift`, `protoc-gen-grpc-swift-2`) — после сборки доступны
`ListUserMedia`, `AlbumApi`, `ListDirectoryDetailed`, `GetPath`, приватность и т.д.

## Сборка

```bash
cd Ios/BarkCloud
xcodebuild -project BarkCloud.xcodeproj \
  -scheme BarkCloud \
  -destination 'platform=iOS Simulator,name=iPhone 17' \
  build
```

> Имя симулятора зависит от установленных Runtime'ов: на текущей машине доступны
> устройства на iOS 26.x (`iPhone 17`, `iPhone Air` и т.д.); `iPhone 16 / iOS 18.2`
> из ранних заметок более недоступен. Список: `xcrun simctl list devices available`.

SPM-пакеты подключены — сгенерённые символы (`Barkcloud_Identity_*`, `Barkcloud_Users_*`, `Barkcloud_Files_*`) компилируются.

## Тесты

Unit-test таргет `BarkCloudTests` (`Ios/BarkCloud/BarkCloudTests/`, host-based,
`@testable import BarkCloud`, shared scheme с TestAction). Сейчас покрывает
обслуживающую логику дискового кеша ([[ios-file-cache]]).

```bash
cd Ios/BarkCloud
xcodebuild test -project BarkCloud.xcodeproj -scheme BarkCloud \
  -destination 'platform=iOS Simulator,name=iPhone 17'
```

## История разработки (все PR закрыты)

- **PR 1** ✅ — Setup: deployment target, каркас (App/Session/Theme), strings catalog, proto config, sync script.
- **PR 2** ✅ — gRPC infra: интерсепторы (auth + 4 device), GrpcManager, AuthRepository.
- **PR 3** ✅ — Login: полный экран с OTP-flow.
- **PR 4** ✅ — Main tabs (TabView, 5 destinations). Переработаны: Галерея/Файлы/Альбомы/Корзина/Настройки (PlaceholderScreen удалён, заменён `ComingSoonScreen`).
- **PR 5** ✅ — Local file browser: Domain, Data (FileManager), UI (CRUD, multi-select, share).
- **PR 6** ✅ — Polish: QuickLook thumbnails, плюралы, snackbar.
- **PR 7** ✅ — Серверная интеграция: multi-endpoint gRPC (Users :7021, Files :7025), `FileTransferService`/`InsecureURLSession`/`RemoteImage`, репозитории `UserRepository`/`CloudRepository`/`AlbumRepository`; экраны Настройки/профиль/приватность/устройства, аватар, медиа-галерея с пагинацией и просмотром, альбомы, облачный файловый менеджер, загрузки фото/видео/документов.

Серверные точки интеграции закрыты — медиа, облако, альбомы и профиль работают с боевым бэкендом.

## Background Upload + Live Activity

См. [[ios-background-upload]] — детальное описание.

Все загрузки (Share / Backup / Manual из Cloud Browser) идут через одну
**background `URLSession`** с identifier `com.barkfluff.BarkCloud.upload` и
`sharedContainerIdentifier = group.com.barkfluff.BarkCloud`. Фактическую передачу
байт ведёт iOS-демон — загрузка переживает сворачивание, kill main app и
перезапуск устройства.

**Ключевые компоненты:**
- `Networking/BackgroundUploadCoordinator.swift` — singleton, держит
  `URLSession.background(...)`, выступает её делегатом (TLS self-signed
  zеркалит `SelfSignedTrustDelegate`, прогресс/завершение → UploadJob).
- `Data/Cache/UploadJob.swift` + `UploadQueueStore.swift` — SwiftData-модель и
  actor поверх неё в App Group container (`UploadQueue.sqlite`). Persist
  переживает kill и доступен Share Extension.
- `Networking/MultipartBodyBuilder.swift` — собирает multipart body как
  файл стримом (background URLSession принимает только `fromFile:`).
- `Networking/UploadLiveActivityController.swift` (`@MainActor`) — управляет
  одной агрегированной Live Activity «Загружаю в BarkCloud» (Lock Screen +
  Dynamic Island), пересчитывает прогресс по всем jobs за последний час.
  **Завершение/скрытие Live Activity** считается по
  `BackgroundUploadCoordinator.blockingActiveJobs(from:)`: активный job держит
  UI, только если недавно прогрессировал (`updatedAt`) **или** его URLSession-task
  ещё жив. Осиротевший `.running` (task умер с прошлым запуском — событий по нему
  уже не будет) исключается, иначе `completed+failed` никогда не сравняется с
  `total` и зомби-Activity висит в Dynamic Island навсегда. `getAllTasks`
  дёргается только при наличии подзависших jobs (горячий путь — без лишних
  системных вызовов).
- `Networking/UploadProgressObserver.swift` (`@MainActor @Observable`) — источник
  глобального баннера над TabBar (`GlobalUploadBanner`). **Источник истины
  раздельный**: для автозагрузки медиатеки баннер зеркалит in-memory счётчики
  `BackupManager` (`uploadDone/uploadFailed/remainingCount/currentFileName`) —
  те же, что [[ios-app#Backup|кнопка облака в Галерее]]. Это намеренно: очередь
  URLSession наполняется порциями (`inFlightLimit`), там в любой момент 1–5 задач,
  и счёт total по ней «застревал на первом файле», хотя в `pendingUpload` десятки.
  `BackupManager` знает весь бэклог и ведёт монотонные счётчики. Реактивность —
  `withObservationTracking` на счётчиках (повторная регистрация из recompute).
  Ручные / share-загрузки по-прежнему считаются из `recentJobs(since:)` (бэкап
  отфильтрован), скрытие — через `blockingActiveJobs`.
- `Shared/UploadActivityAttributes.swift` — `ActivityAttributes`, membership:
  main app + BarkCloudWidgets + ShareExtension.
- `BarkCloudWidgets/UploadLiveActivity.swift` — SwiftUI рендеринг Live Activity
  (compact/expanded/minimal).
- `App/AppDelegate.swift` (через `@UIApplicationDelegateAdaptor`) — принимает
  `handleEventsForBackgroundURLSession` и регистрирует BGTask-хендлер
  `com.barkfluff.BarkCloud.upload.retry`.

**Share Extension** теперь сам инициирует upload: читает токен из shared
Keychain (access group `$(AppIdentifierPrefix)com.barkfluff.BarkCloud`),
`FilesApi.GetUploadUrl` через gRPC, готовит multipart body в App Group,
ставит UploadJob, submit'ит в координатор и стартует Live Activity — затем
закрывается. Демон iOS продолжает загрузку.

**BackupManager** уходит от `cloud.uploadFile(data:)` к
`enqueueAssetForBackup(_:folderID:)` — оригинал ассета пишется потоком в App
Group через `DeviceAssetResource.writeOriginal(asset:to:)` (без RAM),
UploadJob ставится в очередь с лимитом 5 одновременных задач.

**Retry**: при `failed` job координатор зовёт `onPersistentFailure` →
`scheduleRetryBGTaskIfNeeded()` (BGProcessingTaskRequest, требует Wi-Fi,
earliestBeginDate +5 мин). При просыпании BGTask resubmit'ит failed jobs
с `retries < 3`.

**Capabilities**:
- `BarkCloud.entitlements` и `ShareExtension.entitlements`: + `keychain-access-groups`.
- `BarkCloudWidgets.entitlements`: `application-groups`.
- Main app build settings: `INFOPLIST_KEY_NSSupportsLiveActivities = YES`,
  `INFOPLIST_KEY_UIBackgroundModes = processing`,
  `INFOPLIST_KEY_BGTaskSchedulerPermittedIdentifiers = com.barkfluff.BarkCloud.upload.retry`.
- `SessionStore` Keychain queries содержат `kSecAttrAccessGroup` (только не на симуляторе).

**Скрипты pbxproj**: `setup_widgets_target.rb` (создаёт widget target +
Shared/), `setup_share_extension_sources.rb` (добавляет references на
gRPC/Networking/Generated файлы в Share Extension target + линкует SwiftPM
зависимости), `setup_bgtasks_info.rb` (BGTaskScheduler INFOPLIST_KEY).
Скрипты идемпотентны.

`ShareInboxUploader` остался как одноразовая миграция legacy очереди
(`ShareInbox/<uuid>/<file>`) — переоформляет файлы в UploadJob через
`cloud.enqueueBackgroundUpload(sourceFile:)` при старте app.
