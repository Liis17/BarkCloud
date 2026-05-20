# iOS — App

Parent: [[index]]

## Назначение

Нативный iOS-клиент BarkCloud (SwiftUI, Swift 5, iOS 18+). Цель — полный паритет с Android-клиентом: Login+OTP, 5-табовый Main с Files по умолчанию, локальный файл-браузер.

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
│   ├── AppEnvironment.swift        @Observable service locator (sessionStore, localFileRepository)
│   └── RootView.swift              gate: hasValidRefreshToken ? Main : Login
├── Session/
│   └── SessionStore.swift          Keychain (kSecClassGenericPassword, service "com.barkfluff.BarkCloud.tokens")
├── Networking/                     (создаётся в PR 2)
├── Data/Auth/                      (создаётся в PR 2)
├── Features/
│   ├── Login/LoginScreen.swift     (stub, полная реализация — PR 3)
│   ├── Main/MainScreen.swift       (stub, полная реализация — PR 4)
│   ├── Placeholder/                (PR 4)
│   └── Files/                      (PR 5)
│       ├── Domain/
│       ├── Data/LocalFileRepository.swift  (stub, актер с documentsRoot)
│       └── UI/
├── Theme/
│   ├── AppColors.swift             SwiftUI semantic colors (Color.primary/secondary/accentColor)
│   ├── AppTypography.swift         Material 3 size scale через Font.system(size:weight:)
│   └── BarkCloudTheme.swift        ViewModifier с .tint(AppColors.accent)
├── Resources/
│   └── Localizable.xcstrings       Все строки из Android strings.xml, sourceLanguage = "ru"
├── Proto/
│   ├── grpc-swift-proto-generator-config.json
│   └── .gitignore                  (игнорирует синхронизируемые .proto)
└── Assets.xcassets/                AccentColor, AppIcon (от стартера)
```

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

## Зависимости (планируются, не подключены)

| Пакет | URL | Версия |
|---|---|---|
| grpc-swift | `https://github.com/grpc/grpc-swift` | `from 2.0.0` |
| grpc-swift-nio-transport | `https://github.com/grpc/grpc-swift-nio-transport` | `from 2.0.0` |
| grpc-swift-protobuf | `https://github.com/grpc/grpc-swift-protobuf` | `from 2.0.0` |
| swift-protobuf | `https://github.com/apple/swift-protobuf` | `from 1.28.0` |

Keychain — нативный `Security` framework (без сторонних зависимостей).

## Ручные шаги в Xcode UI

См. `Docs/IOS_SETUP.md`:
1. Добавить 4 SPM-пакета через File → Add Package Dependencies.
2. Подключить build-tool plugin `GRPCProtobufGenerator` к target BarkCloud.
3. Добавить Run-Script build phase «Sync Shared Proto» (вызов `sync_proto.sh`) перед «Compile Sources».
4. (Перед PR 2) Создать `Info.plist` с `NSAppTransportSecurity → NSAllowsArbitraryLoads` для dev (self-signed TLS на `localhost:5001`).

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
| `BuildConfig.IDENTITY_API_ADDRESS = https://10.0.2.2:5001` | `https://localhost:5001` (Simulator) — константа в `GrpcManager` (PR 2) |

## Сборка

```bash
cd Ios/BarkCloud
xcodebuild -project BarkCloud.xcodeproj \
  -scheme BarkCloud \
  -destination 'platform=iOS Simulator,name=iPhone 16,OS=18.2' \
  build
```

После PR 1 — сборка проходит (PR 1 не зависит от SPM-пакетов). После добавления SPM (PR 2) — должны компилироваться `Identity_*`, `Users_*`, `Files_*` сгенерённые символы.

## План развития

- **PR 1** ✅ — Setup: deployment target, каркас (App/Session/Theme), strings catalog, proto config, sync script.
- **PR 2** — gRPC infra: 5 interceptor'ов, GrpcManager, AuthRepository.
- **PR 3** — Login: полный экран с OTP-flow.
- **PR 4** — Main tabs (TabView, 5 destinations, PlaceholderScreen).
- **PR 5** — Local file browser: Domain, Data (FileManager), UI (CRUD, multi-select, share).
- **PR 6** — Polish: QuickLook thumbnails, плюралы, snackbar.

См. план в `~/.claude/plans/mellow-waddling-biscuit.md`.
