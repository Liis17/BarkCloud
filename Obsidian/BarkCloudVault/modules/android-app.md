# Android — App

Parent: [[index]] · See also: [[modules/shared-proto]] · [[api/identity-api]] · [[modules/backend-grpcserver]] · [[modules/ios-app]]

## Назначение

Нативный Android-клиент BarkCloud (Kotlin, Jetpack Compose, **Material 3 Expressive**). Достигнут функциональный паритет с [[modules/ios-app]]: вход+OTP, 5 табов как в iOS (**Галерея / Файлы / Альбомы(по умолчанию) / Корзина / Настройки**), облачные медиа с пагинацией, альбомы (CRUD), облачный файл-браузер (CRUD/перемещение/загрузка), корзина, профиль/аватар/приватность/устройства, избранное. gRPC-связь со всеми микросервисами (Identity :7020, Users :7021, Files/Cloud/Album :7025) + HTTP-слой для upload/download/превью по self-signed TLS.

## Реализованный функционал (паритет с iOS)

> Реализовано фазами 1–4F (2026-05-27). Весь код компилируется (`./gradlew :app:assembleDebug`). Подробности и решения — в авто-памяти `android-ios-parity` и плане `bubbly-coalescing-hedgehog.md`.

- **Material 3 Expressive** (`ui/theme/`): `MaterialExpressiveTheme` + `MotionScheme.expressive()`, фирменный seed поверх expressive-схемы + dynamic color (Android 12+). Требует material3 **1.4.0-alpha18** (форс в `app/build.gradle.kts` через `resolutionStrategy`; в стабильной 1.4.0 Expressive-API `internal`).
- **gRPC/сеть** (`grpc/`, `net/`): `GrpcManager` (мульти-эндпоинт, кэш каналов), `GrpcEndpoint.normalizedFileDownloadURL`, `InsecureTls` (общий trust-all), `InsecureHttp` (OkHttp), `FileTransferService` (multipart upload стримингом по Uri / download). Coil настроен на trust-all OkHttp (`OkHttpNetworkFetcherFactory`) для превью с :7025.
- **Данные** (`data/cloud/`, `data/users/`): `CloudModels` (MediaAsset/Album/Trash/Favorite…), `CloudRepository` (медиа/каталоги/корзина/избранное/upload), `AlbumRepository`, `UserRepository`, `SessionManager` (logout+очистка). Зарегистрированы в `BarkCloudApplication`.
- **UI-экраны** (`ui/`): `gallery/` (MediaStore+SHA256-бейдж «в облаке» через `CheckFileHashes`), `media/`+`albums/` (сегменты Фото/Видео/Альбомы, cursor-пагинация, CRUD альбомов, контекстное меню избранного), `files/` (`CloudBrowserScreen` + `CloudMovePicker`), `trash/` (свайпы restore/delete-forever, empty), `settings/` (профиль/аватар/приватность/устройства/выход/удаление), `favorites/`. Общие компоненты — `ui/components/` (`RemoteImage`, `MediaThumb`, `CloudMediaViewer`, `ComingSoonScreen`, `TextInputDialog`, `rememberRemoteOpener`).
- **Навигация** (`ui/main/`): 5 табов через вложенные графы (per-tab back-stack), pill-NavigationBar; sign-out проброшен `RootNavGraph → MainScreen → SettingsScreen`.
- «Общие файлы» → `ComingSoonScreen` (бэкенд не поддерживает расшаривание), как и на iOS.

⚠️ Не проверено в рантайме на устройстве (forward-совместимость material3-alpha с Compose из BOM; реальное поведение self-signed превью/upload). Ниже — описание исходного каркаса (вход + локальный браузер), частично устарело.

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
