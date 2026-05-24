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
│   ├── AppEnvironment.swift        @Observable service locator (sessionStore, grpcManager, authRepository, localFileRepository)
│   └── RootView.swift              gate: hasValidRefreshToken ? Main : Login
├── Session/
│   └── SessionStore.swift          Keychain (kSecClassGenericPassword, service "com.barkfluff.BarkCloud.tokens")
├── Networking/                     gRPC: GrpcManager (actor) + интерсепторы
│   ├── GrpcManager.swift           GrpcEndpoint (cloud.barkfluff.com:7020, TLS, allowSelfSigned), lazy IdentityApi-стаб
│   ├── AuthInterceptor.swift       x-auth-token (динамически)
│   ├── XAppInterceptor / XDeviceInterceptor / XIpInterceptor / XOsInterceptor — device-метаданные
│   ├── Base64Header.swift          base64-кодирование значений заголовков
│   ├── AuthErrorCodes.swift        GUID-коды OTP_REQUIRED / INVALID_CREDENTIALS
│   └── GrpcError.swift             извлечение x-error-code из trailing-metadata
├── Data/Auth/
│   ├── AuthRepository.swift        IdentityApi.Auth, сохранение токенов в SessionStore
│   └── AuthResult.swift            enum: success / otpRequired / invalidCredentials / otherError
├── Features/
│   ├── Login/                      LoginScreen + LoginUiState + LoginViewModel (логин/пароль + OTP)
│   ├── Main/                       MainScreen (TabView, 5 destinations), MainDestination
│   ├── Placeholder/                PlaceholderScreen (табы Shared/Settings)
│   ├── Media/                      сетка Фото/Видео (3 столбика, квадраты, скелетоны)
│   │   ├── MediaKind.swift         enum { photo, video }: titleKey, emptyKey, isVideo
│   │   ├── MediaItem.swift         модель (id, thumbnailURL?, isVideo) + placeholders(count:isVideo:)
│   │   ├── MediaGridViewModel.swift @Observable, isPlaceholder; load() — stub под CloudApi.ListUserImages
│   │   └── MediaGridScreen.swift   LazyVGrid 3 кол. + приватный MediaCell, .redacted в плейсхолдер-режиме
│   └── Files/                      локальный файл-браузер
│       ├── Domain/                 FsEntry, FsSort
│       ├── Data/                   LocalFileRepository (actor), FileShareHelper, MimeIcon, StoragePermission
│       └── UI/                     FilesRootScreen/ViewModel, LocalBrowserScreen/ViewModel, BrowserUiState, FsRowItem, FormatUtils, PickFolderDialog, ThumbnailLoader
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

## Медиа-сетка и серверные папки

Реализован каркас UI поверх ещё не подключённого облачного API (см. [[api/files-api]] → `CloudApi`):

- **Вкладки Фото/Видео** (`Features/Media/`) — переиспользуемый `MediaGridScreen(kind:)`:
  `LazyVGrid` в 3 столбика, ячейки квадратные (`aspectRatio(1, .fit)`, spacing 2pt),
  бейдж видео в углу. Пока данных нет — сетка из 12 серых плиток в режиме
  `.redacted(reason: .placeholder)`. `MediaGridViewModel.load()` — заглушка, точка интеграции —
  `CloudApi.ListUserImages` (cursor-пагинация, для видео фильтр по типу превью).
- **Файлы → секция «Папки с сервера»** — раньше один неинтерактивный card; теперь список папок
  прямо на странице (`FilesRootViewModel`, модель `ServerFolder` ≈ `DirectoryInfo`). Пока ~5
  скелетон-строк с иконкой `folder`. Точка интеграции — `CloudApi.ListDirectory(root)`.

Стиль заглушек — нативный SwiftUI `.redacted(reason: .placeholder)`; при подключении сервера
скелетоны сменятся реальными превью/папками без переписывания вёрстки.

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

Открытые точки интеграции с сервером: `MediaGridViewModel.load()` и серверные папки в Files — заглушки под `CloudApi` ([[api/files-api]]).
