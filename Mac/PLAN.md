# План реализации macOS-клиента (выполнять на Mac)

Пошаговый чеклист для разработки нативного macOS-клиента виртуального диска BarkCloud
(том в Finder через **FSKit**). Обзор и решения — [README.md](README.md); память проекта —
`Obsidian/BarkCloudVault/modules/macos-drive.md`; референс — Windows-клиент `Drive/`.

> **Требования среды:** macOS 15.4+, Xcode 16+, Apple Developer аккаунт (Developer ID для
> подписи/нотаризации). Инструменты генерации proto: `brew install protobuf swift-protobuf grpc-swift`.
> Ветка разработки: `claude/mac-virtual-disk-display-S8lTc`.

Легенда проверки: `✅ verify:` — как убедиться, что шаг выполнен.

---

## Этап 0 — Общий пакет `BarkCloudKit` + миграция iOS

> **СТАТУС: ВЫПОЛНЕН** (ветка `claude/mac-virtual-disk-display-S8lTc`). Сетевой слой перенесён
> в пакет, API сделан `public`, добавлены macOS-ветки (`XDeviceInterceptor`/`XOsInterceptor`,
> `BarkCloudAppGroup`), iOS переведён на `import BarkCloudKit`. `swift build` + `xcodebuild`
> (app/ShareExtension/Widgets) + `build-for-testing` — зелёные. Фоновая загрузка iOS-only
> вынесена в `Ios/.../Data/Cloud/CloudRepository+BackgroundUpload.swift`. iOS-фаза «Sync Shared
> Proto» удалена (proto теперь из пакета). Не сделано: `RangeBlockReaderTests` (см. 0.6) —
> отложен до Этапа 1.

Цель: единый источник правды для сетевого слоя. Сейчас в `Mac/BarkCloudKit/` лежит только
новый код (Range-ридер, батч-удаление) и манифест — пакет **не собирается**, пока не перенесены
файлы из iOS.

### 0.1 Перенести платформо-независимые файлы из iOS в пакет
Переместить (git mv) в `Mac/BarkCloudKit/Sources/BarkCloudKit/` из `Ios/BarkCloud/BarkCloud/`:
- `Networking/`: `GrpcManager.swift` (вкл. `ServerConfig`/`GrpcEndpoint`), `FileTransferService.swift`,
  `InsecureURLSession.swift`, `MultipartBodyBuilder.swift`, `Base64Header.swift`, `GrpcError.swift`,
  `AuthErrorCodes.swift`, `CloudErrorCodes.swift`, `AuthInterceptor.swift`, `XDeviceInterceptor.swift`,
  `XOsInterceptor.swift`, `XAppInterceptor.swift`, `XIpInterceptor.swift`
- `Session/SessionStore.swift`
- `Data/Cloud/`: `CloudRepository.swift`, `CloudModels.swift`, `AlbumRepository.swift`
- `Data/Auth/`: `AuthRepository.swift`, `AuthResult.swift`
- `Data/Users/*`

**НЕ переносить** (iOS-only, остаются в iOS-таргете): `BackgroundUploadCoordinator.swift`,
`UploadLiveActivityController.swift`, `UploadProgressObserver.swift`, `UploadConstants.swift`
(App Group id), всё, что тянет UIKit/ActivityKit/BGTask.

### 0.2 Перегенерировать proto в пакет
```bash
Mac/BarkCloudKit/scripts/sync_proto.sh    # → Sources/BarkCloudKit/Generated/, Visibility=Public
```
Удалить старые `Ios/BarkCloud/BarkCloud/Generated/Proto/*` после перевода iOS на пакет (0.5).

### 0.3 Сделать API публичным
Используемые из app/extension типы и методы → `public`: `GrpcManager`, `SessionStore`,
`ServerConfig`/`GrpcEndpoint`, `FileTransferService`, `CloudRepository`, `AuthRepository`,
модели (`CloudListing`, `CloudFileEntry`, `CloudDirectory`, `CloudFile`…), интерцепторы по
необходимости. В `CloudRepository.swift`: `private let grpc` → `let grpc` (нужно для
`CloudRepository+BatchDelete.swift`).

### 0.4 macOS-ветки платформенного кода
- `XDeviceInterceptor`: имя устройства через `Host.current().localizedName` / `ProcessInfo`
  под `#if os(macOS)` (на iOS было `UIDevice`). Значения заголовков — **Base64(UTF8)**,
  `x-auth-token` — сырой (иначе Auth падает `XDeviceNameIsRequired` и т.п.).
- `ServerConfig.store` и `SessionStore`: App Group id и keychain-access-group, **общие** у
  контейнера и расширения (см. 1.5). На macOS App Group — `group.<TeamID>.com.barkfluff.BarkCloud…`.
- `InsecureURLSession`: проверить, что self-signed-делегат работает на macOS (должен — тот же API).

### 0.5 Подключить пакет к iOS-проекту
- В `BarkCloud.xcodeproj` добавить локальную зависимость `Mac/BarkCloudKit` (Add Local Package).
- Добавить продукт `BarkCloudKit` в таргеты: main app, ShareExtension, Widgets (где использовалось).
- Удалить перенесённые файлы из таргетов; заменить импорты на `import BarkCloudKit`; снять
  `internal`-ограничения, где обращались к ставшим `public` типам.

### 0.6 Тесты пакета
`Tests/BarkCloudKitTests/RangeBlockReaderTests.swift`: локальный HTTP-сервер, отвечающий `206`
с `Content-Range` → проверить сборку файла из блоков и откат на whole при `200`.

**✅ verify Этап 0:**
- `swift build` пакета — успешно; `swift test` — зелёный.
- `xcodebuild` iOS-таргетов собирается; `BarkCloudTests` зелёные; поведение iOS не изменилось
  (логин, галерея, загрузка, share — вручную смоук-тест).

---

## Этап 1 — FSKit-расширение `BarkCloudFS.appex`

> **СТАТУС: read-path скаффолд КОМПИЛИРУЕТСЯ** (`Mac/BarkCloudDrive/`). Проект app+appex создан,
> оба таргета линкуют `BarkCloudKit`, `xcodebuild` зелёный (CODE_SIGNING_ALLOWED=NO). Реализованы
> mount/activate/enumerate/attributes/lookup/reclaim/read (через `RangeBlockReader`) + volumeStatistics.
> Карта реального FSKit API — `Obsidian/BarkCloudVault/modules/macos-fskit-api.md`.
> **Осталось:** write-path (1.5), авторефреш токена (1.6), рантайм-маунт (нужен Team ID + включение
> расширения в System Settings — на стороне пользователя).

Цель: реализовать том, маппящий FS-операции на облако через `BarkCloudKit`.

### 1.1 Создать Xcode-проект `Mac/BarkCloudDrive/`
- App-таргет (контейнер) + таргет расширения типа **File System Module** (`com.apple.fskit.fsmodule`).
- Оба зависят от `BarkCloudKit` (Add Local Package).
- Deployment target: macOS 15.4.

### 1.2 `FSUnaryFileSystem` + `FSUnaryFileSystemOperations`
- `probeResource`/`loadResource` → подготовить ресурс тома (для cloud-FS — unary, без блочного устройства).
- Создать `FSVolume` с меткой «BarkCloud», атрибутами read-write.

### 1.3 `FSVolume.Operations` (маппинг Dokany → FSKit)
Реализовать протоколы операций тома (имена API уточнить по SDK конкретной версии macOS):

| Dokany (`BarkCloudFileSystem.cs`) | FSKit | BarkCloud (`BarkCloudKit`) |
|---|---|---|
| `GetVolumeInformation` | атрибуты тома | метка «BarkCloud», read-write |
| `GetDiskFreeSpace` | `FSStatFSResult` | `FileTransferService.storageInfo()` (used/limit) |
| `FindFiles` | `enumerateDirectory` | `CloudRepository.listDirectory(dirID)` |
| `GetFileInformation` | `getAttributes(of:)` | из кэша листинга (`CloudModels`) |
| `CreateFile`(open) | `lookupItem`/`openItem` | резолв `entryId`/`fileId`, без скачивания |
| `ReadFile(offset)` | `read(...)` | `RangeBlockReader.read(fileID:fileLength:offset:length:)` |
| `WriteFile`+`Cleanup` | `write` + `closeItem` | буфер→`getUploadURL`→`POST /web/upload`→`attachFile` |
| `CreateDirectory` | `createItem(.directory)` | `createDirectory` |
| `DeleteFile`/`DeleteDirectory` | `removeItem` | `batchDeleteFileEntries`/`deleteDirectory` |
| `MoveFile` | `renameItem` | `rename*`/`move*` Directory/FileEntry |

### 1.4 Модель узлов `FSItem`
Дерево `FSItem`-узлов с привязкой `entryId`/`fileId`/`directoryId`, кэш по lookup. Путевой
строковый резолвер (как `CloudGateway.Resolve` на Windows) **не нужен** — FSKit оперирует нодами.

### 1.5 Семантика записи (наследуется от Windows)
- Блобы иммутабельны → правка существующего файла = перезалив целиком на `closeItem`, если
  содержимое менялось; если эффективный `fileId` совпал — no-op.
- Реальный upload/replace — на закрытии item (аналог `Cleanup`), не на каждом `write`.
- Рабочие копии записи и блоки чтения — в `~/Library/Caches/BarkCloud.Drive/`.
- Ошибки синхронизации не глушить → пробрасывать в статус (в дашборд через общее хранилище).

### 1.6 Сессия в расширении
- Расширение читает `ServerConfig` (App Group UserDefaults) и refresh-токен (shared Keychain),
  поднимает свой `GrpcManager` + **фоновый авторефреш** (добавить таймер в `GrpcManager`, как
  `TokenManager.RefreshLoopAsync` на Windows — токен ~30 мин, маунт длинный).
- Entitlements расширения: client networking, App Group, keychain-access-group — **общие с app**.

**✅ verify Этап 1:** включить расширение (System Settings → General → Login Items & Extensions →
File System Extensions), смонтировать том → появляется в Finder; листинг, чтение большого файла
(в логе Range-блоки, не целиком), запись/копирование (виден после ремаунта), удаление пачкой
(`DeleteFileEntries`), mkdir/rename/move.

---

## Этап 2 — Контейнер-приложение `BarkCloud Drive.app`

SwiftUI + menu-bar (`NSStatusItem`), паритет с Windows-`App` (трей + 3 окна).

- **Первый запуск:** экран адреса сервера (host + порты Identity/Users/Files + self-signed) →
  `ServerConfig.persist()` → логин (`AuthRepository`, OTP) → токены в Keychain → имя тома →
  монтирование.
- **Дашборд:** аватар (`UsersApi.GetUser(0)`), имя, сервер, прогресс хранилища, баннер ошибок
  синхронизации, кнопки монтаж/размонтаж/настройки.
- **Menu-bar:** Открыть / Примонтировать / Отмонтировать / Выход.
- **Монтаж/размонтаж:** через FSKit API контейнера (зарегистрировать/смонтировать том своего
  модуля; `unmount`). Имя тома = метка + точка монтирования.
- **Настройки:** разлогин (`SessionStore.clearSession` + `ServerConfig.clear`), смена адреса
  сервера, переименование тома, выбор папки кэша, автозапуск.
- **Автозапуск:** `SMAppService.mainApp.register()` (Login Item).
- **Локализация (RU/EN/DE):** String Catalogs или порт ключей из
  `Drive/BarkCloud.Drive.Contracts/Localization/Strings*.resx`.

**✅ verify Этап 2:** полный цикл первого запуска → монтирование; закрытие окна не размонтирует;
выход размонтирует; перезапуск с восстановленной сессией; смена языка на лету; Login Item
монтирует том при входе в систему.

---

## Этап 3 — Упаковка и автозапуск

- `.pkg`/`.dmg` (`productbuild`/`create-dmg`), подпись **Developer ID** + **нотаризация** (`notarytool`),
  staple. Бандл расширения — внутри `.app`.
- Онбординг: инструкция включить расширение в System Settings → File System Extensions
  (FSKit требует явного включения пользователем) — показать в первом запуске + README.
- Login Item через `SMAppService`.

**✅ verify Этап 3:** установка `.pkg` на чистую систему → онбординг → монтирование; `spctl`/
`stapler validate` проходят; автозапуск при входе.

---

## Риски и заметки

- **FSKit незрелый** (15.4+): имена/сигнатуры `FSVolume.*Operations` уточнять по SDK; Range/write-
  семантику проверять эмпирически — главный технический риск.
- **Device-заголовки обязательны** для Auth (Base64(UTF8); `x-auth-token` — сырой); `x-device-id`
  персистить (на macOS — в App Support/Keychain, стабильный per-install).
- **Бэкенд правок не требует:** Range (206) — `Backend/BarkCloud.Files/Host/FilesController.cs` +
  `S3Uploader.DownloadRangeAsync`; батч — `CloudApi.DeleteFileEntries`.
- **Память проекта:** обновлять `Obsidian/BarkCloudVault/modules/macos-drive.md` по мере фаз.
