# Android — App

Parent: [[index]] · See also: [[modules/shared-proto]] · [[api/identity-api]] · [[modules/backend-grpcserver]] · [[modules/ios-app]]

## Назначение

Нативный Android-клиент BarkCloud (Kotlin, Jetpack Compose, Material 3). Реализованы: вход с поддержкой OTP-шага (2FA), 5-табовый главный экран (Files по умолчанию) и локальный файл-браузер. gRPC-связь с микросервисами через сгенерированные из общих proto-стабы. Полный паритет с [[modules/ios-app]].

## Расположение

`Android/BarkCloud.Android/`

## Структура (фактическая)

```
app/src/main/java/com/barkfluff/BarkCloud/
├── BarkCloudApplication.kt   — ручной service locator + Coil ImageLoader (VideoFrameDecoder)
├── MainActivity.kt           — edge-to-edge, setContent { BarkCloudTheme { RootNavGraph() } }
├── data/
│   ├── GlobalParam.kt        — EncryptedSharedPreferences: access/refresh токены + сроки, hasValidRefreshToken, clearSession
│   └── AuthRepository.kt     — IdentityApi.Auth → AuthResult (Success/OtpRequired/InvalidCredentials/OtherError)
├── grpc/
│   ├── GrpcManager.kt         — lazy identity-канал (OkHttp), TLS с доверием самоподписанному серту
│   ├── AuthInterceptor.kt     — заголовок x-auth-token (динамически, без base64)
│   ├── ClientMetadataInterceptor.kt — x-device-id/name, x-os-name, x-app-name/version, x-ip-address (base64 NO_WRAP)
│   ├── GrpcError.kt           — StatusRuntimeException.errorCode() из трейлера x-error-code
│   └── AuthErrorCodes.kt      — GUID-коды OTP_REQUIRED / INVALID_CREDENTIALS
├── ui/
│   ├── navigation/RootNavGraph.kt — гейт login ↔ main по hasValidRefreshToken()
│   ├── login/                 — LoginScreen, LoginUiState, LoginViewModel (логин/пароль + OTP)
│   ├── main/                  — MainScreen (Scaffold + вложенный NavHost), MainDestination (5 табов), MainBottomBar
│   ├── screens/PlaceholderScreen.kt — заглушка табов Photos/Videos/Shared/Settings
│   └── theme/                 — Color, Shape, Theme, Type (Material 3)
└── files/                     — локальный файл-браузер (см. ниже)
app/src/main/proto/            — синхронизируется из Shared/BarkCloud.Proto (gradle task syncSharedProto)
```

## Service locator (`BarkCloudApplication`)

Зависимости создаются вручную в `onCreate` (без Hilt/Koin), доступны через `applicationContext as BarkCloudApplication`:

- `globalParam: GlobalParam` — хранилище токенов на `EncryptedSharedPreferences` (AES256).
- `grpcManager: GrpcManager` — каналы/стабы gRPC; в конструктор передаётся `ClientMetadataInterceptor.create(this)`.
- `authRepository: AuthRepository` — авторизация.
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
    ├── FilesRootScreen.kt / FilesRootViewModel.kt — корень: запрос разрешения + «папки с сервера» (ServerFolder — заглушка под CloudApi)
    ├── LocalBrowserScreen.kt / LocalBrowserViewModel.kt — навигация по каталогам
    ├── FsRowItem.kt, FormatUtils.kt, PickFolderDialog.kt, rememberThumbnailModel.kt
```

Серверная иерархия (`ServerFolder`) — пока каркас; точка интеграции — `CloudApi.ListDirectory` ([[api/files-api]]).

## gRPC-метаданные клиента

Сервер ([[modules/backend-grpcserver]], `RequestContextInterceptor`) читает метаданные и на части эндпоинтов Identity **требует** заголовки (значения в base64, кроме токена):

- `x-auth-token` — JWT, **без** base64. Добавляет `AuthInterceptor` динамически на каждый запрос.
- `x-device-id`, `x-device-name`, `x-os-name`, `x-app-name`, `x-app-version`, `x-ip-address` — статичны (считаются один раз), base64 `NO_WRAP` (перенос строки сломал бы `Convert.FromBase64String` на сервере). Добавляет `ClientMetadataInterceptor`.

Оба цепляются в `GrpcManager.identityStub()`. Адрес — `BuildConfig.IDENTITY_API_ADDRESS` (`https://cloud.barkfluff.com:7020`); TLS терминируется на nginx ([[structure/infrastructure]]), сертификат самоподписанный → клиент доверяет всем (trust-all `X509TrustManager`). Паритет с iOS, где те же заголовки разнесены по 5 интерсепторам ([[modules/ios-app]]).

Коды ошибок-GUID (`AuthErrorCodes`) приходят в трейлере `x-error-code`; `AuthRepository` транслирует их в `AuthResult`.

## Конфигурация

| Параметр | Значение |
|---------|---------|
| `applicationId` / `namespace` | `com.barkfluff.BarkCloud` |
| `minSdk` | 30 |
| `compileSdk` / `targetSdk` | 36 |
| `versionCode` / `versionName` | 1 / 1.0 |
| Java / jvmTarget | 11 |
| `BuildConfig.IDENTITY_API_ADDRESS` | `https://cloud.barkfluff.com:7020` |

Plugins: `android.application`, `kotlin.android`, `kotlin.compose`, `protobuf` (через version catalog `libs`).

Manifest: разрешения `INTERNET` и `MANAGE_EXTERNAL_STORAGE`; `FileProvider` с authority `${applicationId}.fileprovider` (пути в `res/xml/file_paths.xml`).

## proto / gRPC-сборка

Gradle-таск `syncSharedProto` копирует `**/*.proto` из `Shared/BarkCloud.Proto` в `app/src/main/proto`, откуда `protobuf-gradle-plugin` генерирует java+kotlin **lite** + grpc + grpckt. От таска зависят все `generateProto*`/`extract*Proto`. `resolutionStrategy` пинит `kotlin-stdlib` к версии компилятора (Coil тянет более новый stdlib).

## Зависимости (ключевые)

Compose BOM + material3 + material-icons-extended, navigation-compose, lifecycle-viewmodel/runtime-compose, `androidx.security:crypto`; protobuf javalite + kotlin-lite, grpc okhttp/protobuf-lite/stub/kotlin-stub; kotlinx-coroutines-android; Coil compose + video. Полный список — `gradle/libs.versions.toml`.

## Сборка

```bash
cd Android/BarkCloud.Android
./gradlew assembleDebug
```
