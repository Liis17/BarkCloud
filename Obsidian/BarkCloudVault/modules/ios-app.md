# iOS — App

Parent: [[index]]

## Назначение

Нативный iOS-клиент BarkCloud (SwiftUI, Swift 5, iOS 18+). Полный паритет с Android-клиентом ([[modules/android-app]]): Login+OTP, 5-табовый Main с Files по умолчанию, локальный файл-браузер, медиа-сетки. gRPC через grpc-swift 2. Все PR 1–6 реализованы.

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
│   └── RootView.swift              gate: hasValidRefreshToken ? Main : Login
├── Session/
│   └── SessionStore.swift          Keychain (kSecClassGenericPassword, service "com.barkfluff.BarkCloud.tokens")
├── Networking/                     gRPC: GrpcManager (actor) + интерсепторы
│   ├── GrpcManager.swift           multi-endpoint: Identity :7020 / Users :7021 / Files(Cloud/Album) :7025, TLS allowSelfSigned, кэш GRPCClient по порту, стабы identity/users/files/cloud/album
│   ├── InsecureURLSession.swift    URLSession, доверяющий self-signed TLS (для HTTP upload/download и превью)
│   ├── FileTransferService.swift   FilesApi (GetUploadUrl/GetTempDownloadUrl/StorageInfo) + HTTP multipart upload (поле `file`) / download оригинала
│   ├── CloudErrorCodes.swift       GUID-коды доменных ошибок Files/Users + domainErrorMessage(_:)
│   ├── AuthInterceptor.swift       x-auth-token (динамически)
│   ├── XAppInterceptor / XDeviceInterceptor / XIpInterceptor / XOsInterceptor — device-метаданные
│   ├── Base64Header.swift          base64-кодирование значений заголовков
│   ├── AuthErrorCodes.swift        GUID-коды OTP_REQUIRED / INVALID_CREDENTIALS
│   └── GrpcError.swift             извлечение x-error-code из trailing-metadata
├── Data/Auth/
│   ├── AuthRepository.swift        IdentityApi.Auth, сохранение токенов в SessionStore
│   └── AuthResult.swift            enum: success / otpRequired / invalidCredentials / otherError
├── Data/Users/
│   └── UserRepository.swift        UsersApi: профиль, имя/юзернейм/bio, приватность, устройства, удаление аккаунта, аватар (через FileTransferService)
├── Data/Cloud/
│   ├── CloudModels.swift           доменные модели UI: MediaAsset, MediaPage, CloudDirectory, CloudFileEntry, AlbumCard, PathCrumb (+ Timestamp.date)
│   ├── CloudRepository.swift       CloudApi: ListUserMedia, ListDirectoryDetailed, GetPath, CRUD папок/записей, uploadFile
│   └── AlbumRepository.swift       AlbumApi: список/содержимое альбомов, create/update/delete, add/remove items
├── Features/
│   ├── Login/                      LoginScreen + LoginUiState + LoginViewModel (логин/пароль + OTP)
│   ├── Main/                       MainScreen (TabView, 5 destinations; Settings → SettingsScreen, onSignOut), MainDestination
│   ├── Placeholder/                PlaceholderScreen (только таб Shared)
│   ├── Shared/                     RemoteImage (self-signed AsyncImage-замена + NSCache), FilePreviewController/RemoteFilePreviewScreen (QuickLook), MediaThumb
│   ├── Settings/                   SettingsScreen + ProfileViewModel (профиль/аватар/хранилище/выход/удаление), EditProfileScreen, PrivacySettingsScreen, DevicesScreen
│   ├── Media/                      Фото/Видео: сегмент «Всё / Альбомы»
│   │   ├── MediaKind.swift         enum { photo, video }: titleKey, emptyKey, isVideo
│   │   ├── MediaItem.swift         модель (id=file_id, thumbnailURL?, isVideo, fileName) + init(asset:) + placeholders
│   │   ├── MediaTabScreen.swift    сегмент-контейнер: MediaGridScreen / AlbumsGridScreen
│   │   ├── MediaGridViewModel.swift @Observable: ListUserMedia + cursor-пагинация + загрузка
│   │   ├── MediaGridScreen.swift   LazyVGrid 3 кол. (MediaThumb), PhotosPicker-загрузка, полноэкранный просмотр
│   │   └── Albums/                 AlbumsViewModel, AlbumsGridScreen (карточки), AlbumDetailScreen+VM (items, обложка, add/remove)
│   └── Files/                      файл-браузер (локальный + облачный)
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
| `androidx.navigation.compose NavHost` | SwiftUI `NavigationStack` (Files-таб) + `TabView` (5 табов) |
| `FileProvider + ACTION_SEND` | `UIActivityViewController` через `UIViewControllerRepresentable` (PR 5) |
| `BuildConfig.IDENTITY_API_ADDRESS = https://cloud.barkfluff.com:7020` | `GrpcEndpoint` в `GrpcManager`: `cloud.barkfluff.com:7020`, `useTLS = true`, `allowSelfSigned = true` (TLS терминируется на nginx) |

## Серверная интеграция (реализовано)

Облачный функционал подключён к боевому бэкенду (см. [[api/files-client-guide]], [[api/users-client-guide]]):

- **Настройки** (`Features/Settings/`) — таб «Настройки» вместо заглушки: профиль (`GetUser`),
  аватар через PhotosPicker (`USER_AVATAR` → upload → `SetProfilePicture`; удаление — `SetProfilePicture("")`),
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
- **Вкладки Фото/Видео** (`Features/Media/`) — сегмент «Всё / Альбомы» (`MediaTabScreen`).
  «Всё»: реальная сетка `CloudApi.ListUserMedia(kind)` с cursor-пагинацией и догрузкой при скролле,
  превью через `RemoteImage`, тап → полноэкранный QuickLook (`GetTempDownloadUrl` → download),
  загрузка из PhotosPicker (`GetUploadUrl(CLOUD_FILE)` → HTTP). «Альбомы»: `AlbumApi` —
  карточки (`ListAlbums`), открытие (`ListAlbumItems` с `kind_filter`), создание, добавление файлов,
  смена обложки, удаление элементов/альбома.
- **Файлы → «Облачное хранилище»** — карточка-вход в `CloudBrowserScreen`: навигация по папкам
  (`ListDirectoryDetailed`), хлебные крошки (`GetPath`), CRUD папок/записей, перемещение через
  `CloudMovePicker`, загрузка фото/видео (PhotosPicker) и документов (`.fileImporter`) в текущую папку,
  открытие/скачивание файла в QuickLook.

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
- **PR 4** ✅ — Main tabs (TabView, 5 destinations, PlaceholderScreen).
- **PR 5** ✅ — Local file browser: Domain, Data (FileManager), UI (CRUD, multi-select, share).
- **PR 6** ✅ — Polish: QuickLook thumbnails, плюралы, snackbar.
- **PR 7** ✅ — Серверная интеграция: multi-endpoint gRPC (Users :7021, Files :7025), `FileTransferService`/`InsecureURLSession`/`RemoteImage`, репозитории `UserRepository`/`CloudRepository`/`AlbumRepository`; экраны Настройки/профиль/приватность/устройства, аватар, медиа-галерея с пагинацией и просмотром, альбомы, облачный файловый менеджер, загрузки фото/видео/документов.

Серверные точки интеграции закрыты — медиа, облако, альбомы и профиль работают с боевым бэкендом.
