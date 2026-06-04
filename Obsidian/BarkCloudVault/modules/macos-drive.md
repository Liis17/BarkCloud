[[index]]

# macOS Drive — десктопный виртуальный диск (FSKit)

> Нативный macOS-клиент: монтирует облако BarkCloud как **том в Finder** (боковая панель +
> рабочий стол), содержимое подкачивается по запросу. Аналог [[windows-drive]] (`X:`/Dokany),
> но через **FSKit**. Каталог: `Mac/`. Начато: 2026-06-03.

## Ключевые решения

- **Карта FSKit API (реальные сигнатуры из SDK 26.5): [[macos-fskit-api]]** — читать перед кодом расширения.
- **Движок ФС: FSKit** (`FSUnaryFileSystem`, macOS 15.4+) — нативный Apple-фреймворк, даёт
  настоящий том **без kext**, нотаризуется, App-Store-совместим. Отвергнуты: macFUSE (kext +
  снижение безопасности на Apple Silicon, не App Store) и File Provider (не «том», а облачная
  локация в сайдбаре).
- **Язык: Swift**, переиспользование сетевого слоя iОS ([[ios-app]], grpc-swift 2) через общий
  локальный SwiftPM-пакет **`BarkCloudKit`** (единый источник правды; iOS-таргеты переводятся
  на него же).
- **Бэкенд правок не требует:** Range (206) и `CloudApi.DeleteFileEntries` уже готовы (см.
  [[windows-drive]] «Поблочное чтение» и «Батчинг удаления»).

## Архитектура (соответствие Windows → macOS)

| Windows ([[windows-drive]]) | macOS (`Mac/`) | Роль |
|---|---|---|
| `BarkCloud.Drive.Engine` (Dokany ФС, владелец токенов/refresh) | `BarkCloudFS.appex` (FSKit-расширение `com.apple.fskit.fsmodule`) | Реализует том |
| `BarkCloud.Drive.App` (WPF + трей) | `BarkCloud Drive.app` (SwiftUI + menu-bar `NSStatusItem`) | Настройка, логин, монтаж, дашборд |
| Contracts + named-pipe IPC | App Group + shared Keychain access-group | Передача конфига/токенов app↔extension |
| DPAPI `refresh.bin` | Keychain (`SessionStore`, уже в iOS) | Хранение refresh-токена |
| Dokany driver | FSKit (встроен) + включение в System Settings → File System Extensions | Драйвер ФС |
| Registry `Run` | `SMAppService` (Login Item) | Автозапуск |

**Отличие от Windows:** отдельного «движка-процесса» нет — **FSKit сам поднимает процесс
расширения**. Контейнер-app пишет конфиг/токены в общее хранилище и инициирует mount/unmount;
**refresh-токеном и авторефрешем владеет расширение** (живёт, пока том примонтирован).

## Маппинг Dokany → FSKit

| Dokany (`BarkCloudFileSystem.cs`) | FSKit (`FSVolume.*Operations`) | BarkCloud |
|---|---|---|
| монтирование / `GetVolumeInformation` | `FSUnaryFileSystem` mount / `FSVolume` activate | метка «BarkCloud», read-write |
| `GetDiskFreeSpace` | атрибуты тома (`FSStatFSResult`) | `FileTransferService.storageInfo()` |
| `FindFiles` | `enumerateDirectory` | `CloudRepository.listDirectory` |
| `GetFileInformation` | `getAttributes` | из кэша листинга |
| `CreateFile` (open) | `lookupItem`/`openItem` | резолв `entryId`/`fileId`, без скачивания |
| `ReadFile(offset)` | `read(...)` | `RangeBlockReader` (1 МиБ Range) |
| `WriteFile`/`Cleanup`(write) | `write` + `closeItem` | буфер→`getUploadURL`→`POST /web/upload`→`attachFile` |
| `CreateDirectory` | `createItem(.directory)` | `createDirectory` |
| `DeleteFile`/`DeleteDirectory` | `removeItem` | `batchDeleteFileEntries`/`deleteDirectory` |
| `MoveFile` | `renameItem` | `rename*`/`move*` Directory/FileEntry |

Семантика наследуется от Windows: блобы иммутабельны → правка = перезалив целиком на закрытии;
upload — на закрытии item (не на каждом write); ошибки синхронизации не глушатся.

## Текущее состояние

**Этап 0 (общий пакет `BarkCloudKit`) — ВЫПОЛНЕН** (`Mac/BarkCloudKit/`, ветка
`claude/mac-virtual-disk-display-S8lTc`, 2026-06-03):
- Платформо-независимый сетевой слой iOS перенесён в пакет (`git mv`): `Networking/`
  (gRPC-клиенты, токены, интерцепторы, error-коды, HTTP), `Session/SessionStore.swift`,
  `Data/Cloud|Auth|Users/*`, proto перегенерирован в `Generated/` (Visibility=Public).
- API сделан `public` (репозитории, сервисы, `GrpcManager`/`ServerConfig`/`GrpcEndpoint`,
  модели, `domainErrorMessage`, `InsecureHTTP`, `DomainErrorCodes`, `RPCError.errorCode`).
- macOS-ветки: `XDeviceInterceptor` (`Host.current()`/persisted UUID вместо `UIDevice`),
  `XOsInterceptor` (`ProcessInfo`), новый `BarkCloudAppGroup` (App Group id вместо
  `UploadConstants.appGroupID`).
- iOS-only фоновая загрузка вынесена из пакетного `CloudRepository` в
  `Ios/.../Data/Cloud/CloudRepository+BackgroundUpload.swift` (зависит от `UploadQueueStore`/
  `BackgroundUploadCoordinator`/`UploadConstants` — остаются в iOS-таргете).
- iOS переведён на `import BarkCloudKit` (~40 файлов); `BarkCloud.xcodeproj` через ruby
  `xcodeproj`: добавлен local package, продукт в main app + ShareExtension, удалены ссылки
  `SharedSources` на перенесённые файлы и фаза «Sync Shared Proto».
- Проверки зелёные: `swift build` пакета, `xcodebuild` (app/ShareExtension/Widgets),
  `build-for-testing` (BarkCloudTests компилируется). `RangeBlockReaderTests` отложен (см. PLAN 0.6).
- Новый macOS-код в пакете (для Этапа 1): `RangeBlockReader.swift` (поблочное Range-чтение,
  1 МиБ блоки, дисковый кэш, дедуп, TTL temp-URL 50 мин, откат на whole при ≠206) и
  `CloudRepository+BatchDelete.swift` (`batchDeleteFileEntries`, чанки по 100).

**Этап 1 (FSKit-расширение `BarkCloudFS`) — read-path скаффолд, КОМПИЛИРУЕТСЯ** (`Mac/BarkCloudDrive/`,
2026-06-04). Проект `BarkCloudDrive.xcodeproj`: app-контейнер `BarkCloudDrive` (SwiftUI-заглушка) +
ExtensionKit-расширение `BarkCloudFS` (`com.apple.fskit.fsmodule`), оба линкуют `BarkCloudKit`,
deployment 15.4. `xcodebuild` обеих схем зелёный (CODE_SIGNING_ALLOWED=NO).
- `BarkCloudFSExtension.swift` — `@main UnaryFileSystemExtension`.
- `BarkCloudUnaryFileSystem.swift` — `probe`/`load`/`unload` (load поднимает `BarkCloudSession`).
- `BarkCloudVolume.swift` — `FSVolume.Operations`+`PathConfOperations`+`ReadWriteOperations`:
  activate/deactivate/mount/unmount/sync, attributes, lookup, reclaim, **enumerateDirectory**
  (через `CloudRepository.listDirectory` + packer), **read** (через `RangeBlockReader`),
  volumeStatistics (`storageInfo`). **Write-path (1.5):** createItem (mkdir →
  `createDirectory`; file → отложенный upload), write (рабочая копия на диске),
  open/close (`closeItem` → `uploadFile` на закрытии, блобы иммутабельны), removeItem
  (`deleteFileEntry`/`deleteDirectory`), renameItem (`rename*`/`move*`). Симлинки — `ENOTSUP`.
- `BarkCloudItem.swift` — узел `FSItem` (directory/file, стабильный id-реестр).
- `BarkCloudSession.swift` — ленивый `GrpcManager`/`FileTransferService`/`CloudRepository`/
  `RangeBlockReader` из App Group + Keychain.
- Info.plist расширения: `EXAppExtensionAttributes` (`com.apple.fskit.fsmodule`, `FSShortName`,
  `FSPersonalities`, `FSSupportedSchemes=[barkcloud]`). Entitlements: fskit, app-sandbox, network,
  App Group + keychain (Team ID через `$(TeamIdentifierPrefix)`/`$(AppIdentifierPrefix)`).

**Нужно от пользователя для рантайма** (компиляция не требует): Apple Developer **Team ID** для
подписи + App Group; включить расширение в System Settings → File System Extensions; примонтировать
из контейнер-app (Этап 2) или вручную. **Риск проверить на устройстве:** `FSItem.Identifier(rawValue:)`
для произвольных inode-id (компилируется; если это закрытый enum {0,1,2} — нужен другой носитель id).

**Этап 1 готов в части кода** (read+write компилируются). Авторефреш токена (1.6) покрыт
существующим проактивным рефрешем `GrpcManager.validAccessToken` (срабатывает на каждом FS-запросе);
отдельный фоновый таймер для idle-маунта — опциональное улучшение. **Не сделано (рантайм/устройство):**
маунт и эмпирическая проверка read/write семантики (нужен Team ID + включение расширения).
**Этап 2 (контейнер-app) — код КОМПИЛИРУЕТСЯ** (`Mac/BarkCloudDrive/BarkCloudDrive/`, 2026-06-04):
SwiftUI app + menu-bar (`MenuBarExtra`), переиспользует `BarkCloudKit`.
- `AppModel` — @Observable сервис-контейнер (Grpc/Session/Auth/User/Transfer) + фазы
  serverSetup→login→dashboard (по `ServerConfig.isConfigured` + `hasValidRefreshToken`).
- `ServerSetupView` (хост+порты+self-signed → `ServerConfig.persist`), `LoginView` (auth+OTP),
  `DashboardView` (профиль `getUser`, прогресс `storageInfo`, монтаж/размонтаж), `SettingsView`
  (logout, смена сервера, автозапуск `SMAppService.mainApp`), `MenuBarView`.
- `MountManager` — обёртка `mount`/`umount` (⚠️ механизм FSKit-маунта URL-FS непроверяем, см. ниже).
Локализация RU/EN/DE — **готова** (`Localizable.xcstrings`, 29 ключей, в бандле de/en/ru .lproj),
аватар — **готов** (`RemoteAvatar` через `InsecureHTTP`). Осталось по Этапу 2: реальный механизм
монтирования (рантайм).

**Этап 3 (упаковка) — скрипт готов:** `Mac/BarkCloudDrive/scripts/build_release.sh`
(archive → .dmg/.pkg → подпись Developer ID → `notarytool` → staple), запуск — на стороне
пользователя (нужен Developer ID). Автозапуск — `SMAppService` в настройках.

## Переиспользуемый код iOS ([[ios-app]])

- `Networking/GrpcManager.swift` — gRPC-клиенты + проактивный refresh (`CreateToken`); +фоновый таймер для долгого маунта.
- `Session/SessionStore.swift` — токены в **Keychain** (готовая macOS-замена DPAPI).
- `Networking/FileTransferService.swift` — `getUploadURL`/`tempDownloadURLs`/`storageInfo`/upload(multipart)/download.
- `Networking/InsecureURLSession.swift` — self-signed TLS.
- Интерцепторы `X*Interceptor` — device-заголовки Base64 (обязательны для Auth).
- `Data/Cloud/CloudRepository.swift` — листинг + create/rename/move/delete directory + attach/rename/move/delete file entry.

## План фаз

0. Общий пакет `BarkCloudKit` + миграция iOS ← **ВЫПОЛНЕН** (сборки зелёные)
1. FSKit-расширение `BarkCloudFS` (FSVolume-операции → облако) ← **код read+write компилируется; осталась рантайм-проверка (маунт)**
2. Контейнер-app (server setup, логин, монтаж, дашборд, настройки, автозапуск) ← **код компилируется; осталась локализация + рантайм-монтаж**
3. Упаковка `.pkg`/`.dmg` + нотаризация + онбординг включения расширения ← **скрипт `scripts/build_release.sh` готов; запуск с Developer ID — за пользователем**
