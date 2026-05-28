# iOS — App

Parent: [[index]]

## Назначение

Нативный iOS-клиент BarkCloud (SwiftUI, Swift 5, iOS 18+). gRPC через grpc-swift 2. Login+OTP, серверная интеграция профиля/медиа/облака. 5-табовая навигация: **Галерея** (медиатека устройства, PhotoKit), **Файлы** (устройство + облако + общие), **Альбомы** (default; облачные Фото/Видео/Альбомы), **Корзина** (облако), **Настройки**.

## Расположение

`Ios/BarkCloud/`
- `BarkCloud.xcodeproj/` — Xcode-проект (filesystem-synchronized group → файлы из `BarkCloud/` подхватываются автоматически).
- `BarkCloud/` — корень исходников.
- `sync_proto.sh` — скрипт для Run-Script build phase, синхронизирует `.proto` из `Shared/BarkCloud.Proto/`.

## Текущая структура

```
BarkCloud/
├── App/
│   ├── BarkCloudApp.swift          @main, инжектит AppEnvironment в RootView
│   ├── AppEnvironment.swift        @Observable service locator (sessionStore, grpcManager, authRepository, localFileRepository, fileTransfer, userRepository, cloudRepository, albumRepository)
│   └── RootView.swift              gate: !sessionExpired && (hasValidRefreshToken || isAuthenticated) ? Main : Login
├── Session/
│   └── SessionStore.swift          Keychain (kSecClassGenericPassword, service "com.barkfluff.BarkCloud.tokens"); snapshot()/saveRefreshedAccessToken()/invalidate(), наблюдаемый флаг sessionExpired
├── Networking/                     gRPC: GrpcManager (actor) + интерсепторы
│   ├── GrpcManager.swift           multi-endpoint: Identity :7020 / Users :7021 / Files(Cloud/Album) :7025, TLS allowSelfSigned, кэш GRPCClient по порту, стабы identity/users/files/cloud/album; **проактивное авто-обновление access-токена** (Identity.CreateToken, сериализовано через refreshTask)
│   ├── InsecureURLSession.swift    URLSession, доверяющий self-signed TLS (для HTTP upload/download и превью)
│   ├── FileTransferService.swift   FilesApi (GetUploadUrl/GetTempDownloadUrl/StorageInfo) + HTTP multipart upload (поле `file`) / download оригинала
│   ├── CloudErrorCodes.swift       GUID-коды доменных ошибок Files/Users + domainErrorMessage(_:)
│   ├── AuthInterceptor.swift       x-auth-token — токен берёт у GrpcManager по имени метода (с проактивным refresh; CreateToken → nil, без рекурсии)
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
│   ├── CloudModels.swift           доменные модели UI: MediaAsset, MediaPage, CloudDirectory, CloudFileEntry, AlbumCard, PathCrumb (+ Timestamp.date)
│   ├── CloudRepository.swift       CloudApi: ListUserMedia, ListDirectoryDetailed, GetPath, CRUD папок/записей, uploadFile
│   └── AlbumRepository.swift       AlbumApi: список/содержимое альбомов, create/update/delete, add/remove items
├── Features/
│   ├── Login/                      LoginScreen + LoginUiState + LoginViewModel (логин/пароль + OTP)
│   ├── Main/                       MainScreen (TabView, 5 табов: Галерея/Файлы/Альбомы(default)/Корзина/Настройки), MainDestination
│   ├── Gallery/                    GalleryScreen+VM (медиатека устройства PhotoKit: сетка фото+видео, выбор, загрузка в облако), DeviceMediaViews (PHImageManager-загрузчик + ячейка + полноэкранный просмотр фото/видео), DeviceAssetResource (общее чтение оригинала+SHA256), CloudPresenceTracker (индикация «уже в облаке»), DeviceAssetPickerScreen (кастомный пикер загрузки — замена PhotosPicker)
│   ├── Shared/                     RemoteImage (self-signed AsyncImage-замена + NSCache), FilePreviewController/RemoteFilePreviewScreen (QuickLook), MediaThumb + SquareThumbClip (квадратная обрезка fill-картинки с корректным хит-тестом), ComingSoonScreen (универсальная заглушка «скоро»)
│   ├── Settings/                   SettingsScreen + ProfileViewModel (профиль/аватар/хранилище/выход/удаление), EditProfileScreen, PrivacySettingsScreen, DevicesScreen
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
│   └── Localizable.xcstrings       Все строки из Android strings.xml, sourceLanguage = "ru"
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
| `knownRegions` | `en, ru, Base` |

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
| `BuildConfig.IDENTITY_API_ADDRESS = https://cloud.barkfluff.com:7020` | `GrpcEndpoint` в `GrpcManager`: `cloud.barkfluff.com:7020`, `useTLS = true`, `allowSelfSigned = true` (TLS терминируется на nginx) |

## Серверная интеграция (реализовано)

Облачный функционал подключён к боевому бэкенду (см. [[api/files-client-guide]], [[api/users-client-guide]]):

- **Авто-обновление токена** (`Networking/GrpcManager.swift`, образец — iOS-клиент Barkfluff,
  `BFNetworking/Auth/{AuthInterceptor,TokenRefreshCoordinator}`). Раньше при истечении access-токена
  авторизация «слетала» — обновления не было. Теперь `AuthInterceptor` перед каждым запросом запрашивает
  токен у `GrpcManager.accessToken(forMethod:)`, который **проактивно** обновляет его, если до истечения
  осталось < 60 c (или токена нет): читает снимок `SessionStore.snapshot()`, при необходимости вызывает
  `Identity.CreateToken(refresh_token)` и сохраняет новый access-токен (`saveRefreshedAccessToken`).
  Обновление **сериализовано** полем `refreshTask` (актор-изоляция `GrpcManager`): при пачке параллельных
  запросов `CreateToken` вызывается один раз, остальные ждут результат. Рекурсия исключена — для метода
  `CreateToken` интерсептор не прикрепляет токен и не запускает refresh. Тот же путь (`grpc.validAccessToken()`)
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
  (best-effort, до очистки токенов) → `resetLocalState()` = `SessionStore.clearSession()` (Keychain)
  + `GrpcManager.shutdown()` (сброс кэшированных соединений) + `RemoteImageCache.clear()`
  + `InsecureHTTP.clearCaches()` (URL-кэш/куки) → `onSignOut()` → Login. Удаление аккаунта
  использует `resetLocalState()` без серверного `Logout` (аккаунт уже удалён). На время операции —
  блокирующий оверлей (`isProcessing`), защищающий от повторных нажатий.
- **Галерея** (`Features/Gallery/`) — таб №1, локальная медиатека устройства через **PhotoKit**
  (`PHAsset`, разрешение `NSPhotoLibraryUsageDescription` в build-settings pbxproj). Сетка фото+видео
  (`PHCachingImageManager`), тап → полноэкранный просмотр. **Фото** показываются через
  **QuickLook** (`FilePreviewController`) — тот же просмотрщик, что в Альбомах/облачном браузере, поэтому
  доступны нативные фишки iOS: выделение объекта на фото (Visual Look Up / subject lifting), Live Text,
  зум, шаринг. Для этого `DeviceMediaImageLoader.exportPhotoToTempFile(for:)` потоково выгружает оригинал
  ассета во временный файл (`PHAssetResourceManager.writeData`, приоритет ресурсов как при загрузке —
  имя сохраняет расширение, чтобы QuickLook определил тип) и отдаёт URL в `FilePreviewController`.
  **Видео** остаётся на `requestPlayerItem`+`VideoPlayer` (тяжёлые файлы на диск не гоняем).
  Режим выбора → загрузка выбранных в облако (`DeviceAssetResource.originalData` → `CloudRepository.uploadFile`).
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
- **Альбомы** (`Features/Media/`, таб №3, по умолчанию) — `CloudMediaScreen` с переключателем
  **Фото / Видео / Альбомы**. Фото/Видео: `CloudApi.ListUserMedia(kind)` с cursor-пагинацией и догрузкой,
  превью через `RemoteImage`, тап → полноэкранный QuickLook (`GetTempDownloadUrl` → download),
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
  (`.refreshable` → `reload()`), работает и на пустом состоянии.
- **Корзина** (`Features/Trash/`, таб №4) — `CloudApi.ListTrash` с cursor-пагинацией, превью/иконка
  по типу, дата удаления и срок очистки; свайп — `RestoreFromTrash` / `DeleteFromTrash`; в тулбаре —
  `EmptyTrash` с подтверждением и блокирующим оверлеем. Pull-to-refresh (`.refreshable` → `reload()`),
  работает и на пустом состоянии (пустой экран обёрнут в `ScrollView`).
- **Файлы** (`Features/Files/`, таб №2) — секции: «На устройстве» (`LocalBrowserScreen`),
  «Облачное хранилище» (карточка-вход в `CloudBrowserScreen`: навигация по папкам
  `ListDirectoryDetailed`, хлебные крошки `GetPath`, CRUD папок/записей, перемещение через
  `CloudMovePicker`, загрузка фото/видео (PhotosPicker) и документов (`.fileImporter`), открытие/скачивание
  в QuickLook, pull-to-refresh `.refreshable` → `reload()` (и на пустой папке через `ScrollView`))
  и «Общие файлы» → `ComingSoonScreen` (на бэкенде нет API расшаривания — заглушка «скоро»).

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
  -destination 'platform=iOS Simulator,name=iPhone 16,OS=18.2' \
  build
```

SPM-пакеты подключены — сгенерённые символы (`Barkcloud_Identity_*`, `Barkcloud_Users_*`, `Barkcloud_Files_*`) компилируются.

## История разработки (все PR закрыты)

- **PR 1** ✅ — Setup: deployment target, каркас (App/Session/Theme), strings catalog, proto config, sync script.
- **PR 2** ✅ — gRPC infra: интерсепторы (auth + 4 device), GrpcManager, AuthRepository.
- **PR 3** ✅ — Login: полный экран с OTP-flow.
- **PR 4** ✅ — Main tabs (TabView, 5 destinations). Переработаны: Галерея/Файлы/Альбомы/Корзина/Настройки (PlaceholderScreen удалён, заменён `ComingSoonScreen`).
- **PR 5** ✅ — Local file browser: Domain, Data (FileManager), UI (CRUD, multi-select, share).
- **PR 6** ✅ — Polish: QuickLook thumbnails, плюралы, snackbar.
- **PR 7** ✅ — Серверная интеграция: multi-endpoint gRPC (Users :7021, Files :7025), `FileTransferService`/`InsecureURLSession`/`RemoteImage`, репозитории `UserRepository`/`CloudRepository`/`AlbumRepository`; экраны Настройки/профиль/приватность/устройства, аватар, медиа-галерея с пагинацией и просмотром, альбомы, облачный файловый менеджер, загрузки фото/видео/документов.

Серверные точки интеграции закрыты — медиа, облако, альбомы и профиль работают с боевым бэкендом.
