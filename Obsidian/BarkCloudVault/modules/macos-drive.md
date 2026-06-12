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
- `BarkCloudItemCache.swift` — `actor` cache `identifier → DirInfo/FileInfo`
  с persistent JSON (`items-cache.json` в App Group), заполняется при
  enumerate. Хранит пары имён cloud/local (`name`/`localName`), даты и URL
  превью для миниатюр. На cache miss — `.noSuchItem`, fileproviderd
  перезапросит листинг родителя.
- `BarkCloudEnumerator.swift` — per-container enumerator + EmptyEnumerator
  (working-set/trash) + PendingEnumerator (резолв подпапки из actor-кэша) +
  **`LocalNameAllocator`** — санитизация и дедупликация имён (см. ниже).
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
  enable (`add` для новой регистрации, `reconnect` если уже зарегистрирован
  и был приостановлен), disable (`disconnect(.temporary)` — sync паузируется,
  materialized файлы остаются на диске), purge (`remove` — жёсткое удаление
  с очисткой replica, для logout/смены сервера), revealInFinder (через
  `getUserVisibleURL(.rootContainer)` + `NSWorkspace.activateFileViewerSelecting`).
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

**Ревизия 2026-06-11 — фикс «не все файлы отображаются» + добротность:**
- **`LocalNameAllocator`** (`BarkCloudEnumerator.swift`): бэкенд хранит имена
  как есть (уникальность — байтовая и только среди файлов при attach), а
  fileproviderd молча отбрасывает item'ы с «/» в имени и с коллизиями
  (регистр `Photo.JPG`/`photo.jpg`, NFC/NFD-юникод, файл против папки) — они
  «не отображались» в Finder. Теперь имена санитизируются («/»→«:», без
  control chars, ≤255 байт, непустые) и дедуплицируются внутри контейнера
  суффиксом « (2)» (нумерация стабильна — листинг отсортирован по имени).
  Пара cloud/local имени живёт в кэше; rename сравнивает с local-именем.
- **`findEntry` по `fileID`**: attach мог авто-переименовать файл (« (1)»),
  и поиск по имени цеплял чужую запись — теперь матч по fileID (инвариант
  «один блоб — одна живая запись» делает его однозначным).
- **`createItem`**: `.DS_Store`/`._*`/`.localized` → `.excludedFromSync`
  (не засоряют облако); `.mayAlreadyExist` (реимпорт после сброса replica)
  ищет существующий item по имени — иначе upload дедуплицировался по хешу и
  attach падал `FileAlreadyAttached`.
- **`modifyItem`**: при сбое перезаписи содержимого старая запись
  восстанавливается из корзины (`restoreFromTrash`) — раньше файл терялся;
  неразрешимый новый родитель (например `.trashContainer`) → ошибка вместо
  ложного успеха; исправлено сравнение родителя папки (dirID vs identifier).
- **Миниатюры**: `NSFileProviderThumbnailing` — Finder получает превью
  фото/видео с бэкенда (URL кэшируется при enumerate), без скачивания
  оригиналов.
- **`FileTransferService.download`** (Kit, общий с iOS): каждая загрузка — в
  собственную UUID-поддиректорию tmp (гонка параллельных скачиваний с
  одинаковым именем), suggestedName чистится от «/».
- **`CloudDirectory`** (Kit): + `createdAt`/`updatedAt` из `DirectoryInfo` —
  папки в Finder показывают реальные даты, а не момент энумерации.

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

- **Копия файла внутри диска невозможна по модели бэкенда.** Finder-копия
  дублирует содержимое → upload дедуплицируется по хешу → attach падает
  `FileAlreadyAttached` (инвариант «один блоб владельца — одна запись»).
  Finder покажет ошибку синхронизации, локальная копия останется. Лечится
  только на бэкенде (разрешить N записей на блоб или copy-RPC).
- **`enumerateChanges` без incremental sync.** Глобальный anchor: любая
  локальная мутация инвалидирует все контейнеры → полный `enumerateItems`.
  Для пуш-обновлений с других клиентов понадобится бэкенд-стрим изменений и
  нормальный `currentSyncAnchor` (хотя бы per-container).
- **Большие файлы и память.** `createItem`/`modifyItem` читают файл целиком в
  `Data` и клеят multipart в памяти; у многогигабайтных файлов будет пик RSS.
  Нужен streaming upload (URLSession uploadTask с файлом).
- **Очень большие папки.** `ListDirectoryDetailed` не пагинируется; на тысячах
  файлов ответ может упереться в лимит receive-message gRPC — мониторить.
- **Прогресс в Finder.** `fetchContents`/`createItem` возвращают фиктивный
  `Progress` — у больших файлов индикатор неинформативен. Можно пробросить
  прогресс URLSession.

## План фаз

0. Общий пакет `BarkCloudKit` + миграция iOS ← **ВЫПОЛНЕН**
1. File Provider-расширение `BarkCloudFS` ← **код read+write компилируется;
   осталась рантайм-проверка**
2. Контейнер-app (server setup, логин, регистрация домена, дашборд, настройки,
   автозапуск) ← **код компилируется; осталась рантайм-проверка**
3. Упаковка `.pkg`/`.dmg` + нотаризация ← **скрипт готов; запуск с Developer
   ID — за пользователем**
