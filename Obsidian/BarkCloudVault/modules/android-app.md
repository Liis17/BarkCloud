# Android — App

Parent: [[index]]

## Назначение

Нативный Android-клиент BarkCloud. Сейчас — **пустая стандартная заготовка** от Android Studio: только манифест, ресурсы (иконки, темы, цвета) и пара ExampleTest-классов. Бизнес-логики, экранов и gRPC-стабов пока нет.

## Расположение

`Android/BarkCloud.Android/`

## Структура (фактическая)

```
BarkCloud.Android/
├── build.gradle.kts          — root gradle (Kotlin DSL)
├── settings.gradle.kts       — rootProject.name = "BarkCloud", include(":app")
├── gradle.properties
├── gradlew, gradlew.bat
├── local.properties          — пути к локальному SDK
├── gradle/                   — wrapper и version catalog (libs.versions.toml)
└── app/
    ├── build.gradle.kts
    └── src/
        ├── main/
        │   ├── AndroidManifest.xml
        │   ├── java/com/barkfluff/BarkCloud/   — ПУСТО (нет .kt файлов)
        │   └── res/
        │       ├── drawable/      — ic_launcher_background.xml, ic_launcher_foreground.xml
        │       ├── mipmap-*/       — ic_launcher (5 densities) + round
        │       ├── values/         — colors.xml, strings.xml, themes.xml
        │       ├── values-night/   — themes.xml
        │       └── xml/            — backup_rules.xml, data_extraction_rules.xml
        ├── test/java/com/barkfluff/BarkCloud/ExampleUnitTest.kt
        └── androidTest/java/com/barkfluff/BarkCloud/ExampleInstrumentedTest.kt
```

## Конфигурация

| Параметр | Значение |
|---------|---------|
| `applicationId` | `com.barkfluff.BarkCloud` |
| `namespace` | `com.barkfluff.BarkCloud` |
| `minSdk` | 35 |
| `compileSdk` | 36 (minorApiLevel 1) |
| `targetSdk` | 36 |
| `versionCode` | 1 |
| `versionName` | 1.0 |
| `sourceCompatibility` / `targetCompatibility` | Java 11 |

Plugin: `alias(libs.plugins.android.application)` — через version catalog (`libs`).

## Зависимости (из `app/build.gradle.kts`)

- `androidx.appcompat`
- `androidx.core.ktx`
- (полный список — в `gradle/libs.versions.toml`)

gRPC/Protobuf-зависимости **пока не подключены**. При интеграции с Backend контракты из [[modules/shared-proto]] нужно будет компилировать в Kotlin-стабы.

> ⚠️ Раздел выше устарел: gRPC уже подключён (`grpc/GrpcManager.kt`, генерация стабов в `build.gradle.kts`).

## gRPC-метаданные клиента

Сервер ([[modules/backend-grpcserver]], `RequestContextInterceptor`) читает из метаданных и на части эндпоинтов Identity **требует** заголовки (значения в base64, кроме токена):

- `x-auth-token` — JWT, **без** base64. Добавляет `grpc/AuthInterceptor.kt` (динамически на каждый запрос).
- `x-device-id`, `x-device-name`, `x-os-name`, `x-app-name`, `x-app-version`, `x-ip-address` — статичны, считаются один раз. Добавляет `grpc/ClientMetadataInterceptor.kt` (base64 `NO_WRAP` — иначе перенос строки ломает `Convert.FromBase64String` на сервере).

Оба интерсептора цепляются в `GrpcManager`. Это паритет с iOS, где те же заголовки разнесены по 5 отдельным интерсепторам ([[modules/ios-app]]).

## План документации при росте проекта

Когда появятся реальные исходники, разбивай:
- `modules/android-app-data.md` — слой данных (gRPC-клиенты, БД, репозитории)
- `modules/android-app-domain.md` — use-cases, модели
- `modules/android-app-ui.md` — экраны (Compose/View?), навигация
- `modules/android-app-di.md` — DI (Hilt/Koin)

Каждый — с `Parent: [[modules/android-app]]`.

## Сборка

```bash
cd Android/BarkCloud.Android
./gradlew assembleDebug
```
