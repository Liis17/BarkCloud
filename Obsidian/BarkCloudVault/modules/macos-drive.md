[[index]]

# macOS Drive — десктопный клиент облака (File Provider)

> Нативный macOS-клиент: монтирует облако BarkCloud как **папку в Finder**
> (Locations + `~/Library/CloudStorage/BarkCloud`), содержимое подкачивается
> по запросу. Аналог [[windows-drive]] (`X:`/Dokany), но через
> **NSFileProviderReplicatedExtension**. Каталог: `Mac/`. Начато: 2026-06-03.
> Миграция с FSKit на File Provider: 2026-06-04.

## Ключевые решения

- **API: NSFileProviderReplicatedExtension** (macOS 11+, у нас deployment
  15.4). Тот же механизм, что у iCloud Drive, Dropbox, Google Drive
  (новый клиент), OneDrive.
- **Не FSKit:** капабилити `com.apple.developer.fskit.fsmodule` не
  поддерживается Personal Apple Developer team (нужен платный аккаунт).
  File Provider даёт ту же UX без специальных entitlements. Отвергнуты
  также macFUSE (kext, не App-Store-совместимо) и FUSE-T (GPL/коммерческая
  лицензия, сторонняя зависимость).
- **Язык: Swift**, переиспользование сетевого слоя iOS ([[ios-app]], grpc-swift 2)
  через общий локальный SwiftPM-пакет **`BarkCloudKit`** (единый источник
  правды; iOS-таргеты переведены на него же).
- **Бэкенд правок не требует** — те же API, что у [[windows-drive]].

## Архитектура (соответствие Windows → macOS)

| Windows ([[windows-drive]]) | macOS (`Mac/`) | Роль |
|---|---|---|
| `BarkCloud.Drive.Engine` (Dokany ФС) | `BarkCloudFS.appex` (File Provider, `com.apple.fileprovider-nonui`) | Реализует папку облака |
| `BarkCloud.Drive.App` (WPF + трей) | `BarkCloud Drive.app` (SwiftUI + menu-bar `MenuBarExtra`) | Настройка, логин, домен, дашборд |
| — | `BarkCloudWidgets.appex` (WidgetKit, `com.apple.widgetkit-extension`) | Виджет квоты в Notification Center / на Desktop |
| Contracts + named-pipe IPC | App Group + shared Keychain access-group | Передача конфига/токенов app↔extension |
| DPAPI `refresh.bin` | Keychain (`SessionStore`) | Хранение refresh-токена |
| Dokany driver | NSFileProvider (встроен) + `NSFileProviderManager.add(domain:)` | Драйвер папки |
| Registry `Run` | `SMAppService` (Login Item) | Автозапуск |

**Отличие от Windows:** отдельного «движка-процесса» нет — **системный
демон `fileproviderd` сам поднимает процесс расширения** по запросу.
Контейнер-app пишет конфиг/токены в общее хранилище и регистрирует домен;
**refresh-токеном и авторефрешем владеет расширение** (живёт, пока
fileproviderd удерживает домен).

## Маппинг Dokany → File Provider

| Dokany (`BarkCloudFileSystem.cs`) | File Provider (`NSFileProviderReplicatedExtension`) | BarkCloud |
|---|---|---|
| монтирование | `NSFileProviderManager.add(domain:)` | контейнер-app: enable |
| `FindFiles` | `NSFileProviderEnumerator.enumerateItems` | `CloudRepository.listDirectory` |
| `GetFileInformation` | `NSFileProviderReplicatedExtension.item(for:)` | из in-memory кэша |
| `CreateFile`(open) | (нет аналога — материализация по `fetchContents`) | — |
| `ReadFile(offset)` | `fetchContents(for:version:request:)` — **полная материализация** | `tempDownloadURLs` + `download` |
| `WriteFile`+`Cleanup` | `createItem` / `modifyItem` (с `.contents`) | блоб иммутабелен → delete entry + upload + attach |
| `CreateDirectory` | `createItem(... contentType: .folder)` | `createDirectory` |
| `DeleteFile`/`DeleteDirectory` | `deleteItem` | `deleteFileEntry`/`deleteDirectory` |
| `MoveFile` | `modifyItem` с `.filename`/`.parentItemIdentifier` | `rename*`/`move*` |

**Отличие от FSKit/FUSE/Dokany:** File Provider материализует файл
**целиком** (потом отдаёт системе как обычный POSIX-файл). Range-чтения «на
лету» нет. `RangeBlockReader` из пакета пока не используется в read-path —
оставлен как задел на будущее, если понадобится chunked-download внутри
`fetchContents` для прогресса больших файлов.

Семантика записи наследуется от Windows: блобы иммутабельны → правка
содержимого = `deleteFileEntry` (старого) + `uploadFile` + `attachFile`
как нового. `entryID`/identifier при этом меняется, fileproviderd
подменяет старый item на новый.

## Текущее состояние

**Этап 0 (общий пакет `BarkCloudKit`) — ВЫПОЛНЕН** (`Mac/BarkCloudKit/`,
ветка `claude/mac-virtual-disk-display-S8lTc`, 2026-06-03). Подробно — без
изменений в этой ревизии.

**Этап 1 (File Provider-расширение `BarkCloudFS`) — read+write КОМПИЛИРУЮТСЯ**
(`Mac/BarkCloudDrive/`, миграция 2026-06-04). Проект `BarkCloudDrive.xcodeproj`:
app-контейнер + app-extension `BarkCloudFS` (`com.apple.fileprovider-nonui`),
оба линкуют `BarkCloudKit`, deployment 15.4, оба `xcodebuild` зелёные
(CODE_SIGNING_ALLOWED=NO). Файлы:
- `BarkCloudFileProvider.swift` — `NSFileProviderReplicatedExtension`,
  `loadServices()` lazy (@MainActor, gRPC + Keychain из App Group),
  `item(for:)`, `enumerator(for:)`, `fetchContents`, `createItem`,
  `modifyItem`, `deleteItem`.
- `BarkCloudFileProviderItem.swift` — `NSFileProviderItem`, identifier
  `"d:<dirID>"` / `"f:<entryID>"` / `.rootContainer`, `itemVersion`
  (contentVersion=fileID, metadataVersion=name+parent+modified),
  capabilities per-type.
- `BarkCloudItemCache.swift` — `actor` cache `identifier → CloudDirectory/
  CloudFileEntry`, заполняется при enumerate. На cache miss — `.noSuchItem`,
  fileproviderd перезапросит листинг родителя.
- `BarkCloudEnumerator.swift` — per-container enumerator + EmptyEnumerator
  (working-set/trash) + PendingEnumerator (резолв подпапки из actor-кэша).
- Info.plist: `NSExtension.NSExtensionPointIdentifier =
  com.apple.fileprovider-nonui`, `NSExtensionPrincipalClass =
  ...BarkCloudFileProvider`, `NSExtensionFileProviderSupportsEnumeration = YES`.
- Entitlements: app-sandbox, network, App Group, keychain. **БЕЗ**
  `com.apple.developer.fskit.fsmodule`.

**Этап 2 (контейнер-app) — КОМПИЛИРУЕТСЯ** (`Mac/BarkCloudDrive/BarkCloudDrive/`).
SwiftUI app + menu-bar (`MenuBarExtra`), переиспользует `BarkCloudKit`.
- `AppModel` — @Observable сервис-контейнер + фазы serverSetup→login→dashboard.
- `ServerSetupView`, `LoginView`, `DashboardView`, `SettingsView`, `MenuBarView`.
- **`FileProviderDomainManager`** (вместо FSKit `MountManager`): refreshState,
  enable (`NSFileProviderManager.add(domain:)`), disable (`remove`),
  revealInFinder (через `getUserVisibleURL(.rootContainer)` +
  `NSWorkspace.activateFileViewerSelecting`).
- Локализация RU/EN/DE — `Localizable.xcstrings` (29 ключей), аватар —
  `RemoteAvatar` через `InsecureHTTP`.

**Этап 3 (упаковка) — скрипт готов:** `Mac/BarkCloudDrive/scripts/build_release.sh`.

**Этап 4 (виджет хранилища `BarkCloudWidgets`) — КОМПИЛИРУЕТСЯ + ВСТРОЕН**
(`Mac/BarkCloudDrive/BarkCloudWidgets/`, добавлен 2026-06-04). Виджет читает
квоту из App Group `UserDefaults` (ключи `storage_widget.used/limit/updatedAt`);
контейнер-app пишет через `StorageWidgetBridge` после `loadProfile()`. Размеры
`.systemSmall` и `.systemMedium`, та же визуальная палитра, что в iOS-виджете
([[ios-app]] / `Ios/BarkCloud/BarkCloudWidgets/StorageWidget.swift`): акцент-
оранжевый, переход в красный при ≥ 90 %, капсульный прогресс-бар. Bundle ID
`com.barkfluff.BarkCloud.Drive.Widgets`, App Group ID c TeamID-префиксом через
`INFOPLIST_KEY_BarkCloudAppGroupID`. Кнопка обновления — `RefreshStorageIntent`
(AppIntent), поднимает gRPC прямо в процессе виджета (адрес/токены из shared
storage), пишет свежий снимок и просит `WidgetCenter.reloadTimelines`.

**Нужно от пользователя для рантайма** (компиляция не требует): любой Apple
Developer Team ID (даже Personal — в отличие от FSKit). Эмпирически проверить
read/write/listing в Finder, поведение cache после рестарта `fileproviderd`.

## Переиспользуемый код iOS ([[ios-app]])

- `Networking/GrpcManager.swift` — gRPC-клиенты + проактивный refresh
  (`CreateToken`); для долгоживущего расширения этого достаточно
  (срабатывает на каждом запросе File Provider).
- `Session/SessionStore.swift` — токены в **Keychain** (готовая macOS-замена
  DPAPI).
- `Networking/FileTransferService.swift` — `getUploadURL`/`tempDownloadURLs`/
  `storageInfo`/`upload(multipart)`/`download`.
- `Networking/InsecureURLSession.swift` — self-signed TLS.
- Интерцепторы `X*Interceptor` — device-заголовки Base64 (обязательны для Auth).
- `Data/Cloud/CloudRepository.swift` — листинг + create/rename/move/delete
  directory + attach/rename/move/delete file entry.
- `RangeBlockReader.swift` — пока не задействован в read-path (File Provider
  материализует целиком), задел для chunked-download.

## Открытые вопросы / риски

- **Persistent cache.** `BarkCloudItemCache` — in-memory actor. После рестарта
  `fileproviderd` cache пустой; обычно сразу делается enumerate корня и
  вглубь, восстанавливая cache. Если пин/recents в Finder обращаются к item'у
  не из enumerate-цепочки — потребуется persistent cache (App Group
  UserDefaults или SQLite).
- **`findEntry` после upload.** После `cloud.uploadFile` бэкенд не возвращает
  `entryID`, поэтому делаем `listDirectory(parentDirID)` и ищем по
  `fileID + name`. Эпизодически возможна гонка — мониторить, при
  необходимости добавить proto-метод «attach и верни entryID».
- **`enumerateChanges` без incremental sync.** Сейчас возвращаем «никаких
  изменений», что заставляет fileproviderd периодически делать полный
  `enumerateItems`. Для пуш-обновлений с других клиентов понадобится
  бэкенд-стрим изменений и нормальный `currentSyncAnchor`.

## План фаз

0. Общий пакет `BarkCloudKit` + миграция iOS ← **ВЫПОЛНЕН**
1. File Provider-расширение `BarkCloudFS` ← **код read+write компилируется;
   осталась рантайм-проверка**
2. Контейнер-app (server setup, логин, регистрация домена, дашборд, настройки,
   автозапуск) ← **код компилируется; осталась рантайм-проверка**
3. Упаковка `.pkg`/`.dmg` + нотаризация ← **скрипт готов; запуск с Developer
   ID — за пользователем**
