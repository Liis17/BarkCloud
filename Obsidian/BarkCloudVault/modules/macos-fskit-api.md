[[macos-drive]]

# FSKit API (macOS 26.5 SDK) — карта для BarkCloudFS

> Разведка SDK `FSKit.framework` (2026-06-04). Swift-API почти весь в Objective-C заголовках
> (`.../FSKit.framework/Versions/A/Headers/`), мостится в Swift через `NS_SWIFT_NAME`/
> `NS_SWIFT_ASYNC_NAME`. Доступно с **macOS 15.4+**, только macOS. Используется на Этапе 1
> ([[macos-drive]]) для `BarkCloudFS.appex`.

## Точка входа расширения

- `@main`-тип конформит `FSKit.UnaryFileSystemExtension` (это `ExtensionFoundation.AppExtension`):
  - `associatedtype FileSystem: FSUnaryFileSystem & FSUnaryFileSystemOperations`
  - `var fileSystem: FileSystem { get }`
  - `configuration` отдаётся фреймворком автоматически (extension даёт его через extension на протоколе).
- `import FSKit` + `import ExtensionFoundation`.

## FSUnaryFileSystem (subclass) + FSUnaryFileSystemOperations

Унарная ФС = один том на один «ресурс» (у нас — облако, без блочного устройства).

- `probeResource(resource:) async throws -> FSProbeResult` — узнаёт ресурс. Вернуть
  `FSProbeResult.usable(name: "BarkCloud", containerID:)` / `.usableButLimited(...)` / `.notRecognized`.
- `loadResource(resource:options:) async throws -> FSVolume` — создать и вернуть `FSVolume`.
  `options` (`FSTaskOptions`): `-f` (force), `--rdonly` (read-only — **запомнить** флаг).
- `unloadResource(resource:options:) async throws`.
- (`FSFileSystemBase`: `var containerStatus: FSContainerStatus`.)

## Ресурсы (`FSResource` и подклассы)

- `FSBlockDeviceResource` — блочное устройство (НЕ наш кейс).
- **`FSGenericURLResource(url:)`** — ресурс-URL без блочного устройства → **наш выбор** для cloud-FS
  (URL несёт идентификатор «устройства»/сервера; контейнер-app регистрирует том с таким ресурсом).
- `FSPathURLResource(url:)` — путь-URL.

## FSVolume (subclass) + протоколы операций

`FSVolume` init: `initWithVolumeID:volumeName:`. Конформить:

### `FSVolume.Operations` (`FSVolumeOperations`, обязателен; расширяет `FSVolumePathConfOperations`)
Свойства: `supportedVolumeCapabilities: FSVolumeSupportedCapabilities`, `volumeStatistics: FSStatFSResult`.
Async-методы (Swift-имена):
- `activate(options:) async throws -> FSItem` — вернуть **корневой** `FSItem` (FSKit кэширует его).
- `deactivate(options:) async throws`
- `mount(options:) async throws` / `unmount() async`
- `synchronize(flags:) async throws`
- `attributes(_ request: FSItemGetAttributesRequest, of item: FSItem) async throws -> FSItemAttributes`
- `setAttributes(_ request: FSItemSetAttributesRequest, on item: FSItem) async throws -> FSItemAttributes`
- `lookupItem(named: FSFileName, inDirectory: FSItem) async throws -> (FSItem, FSFileName)`
- `reclaimItem(_ item: FSItem) async throws` — освободить ресурсы узла (не сам unlink).
- `createItem(named:type:inDirectory:attributes:) async throws -> (FSItem, FSFileName)` — file/directory.
- `removeItem(_ item: FSItem, named: FSFileName, fromDirectory: FSItem) async throws` — только разорвать
  имя в каталоге; удаление узла — в `reclaimItem`.
- `renameItem(_:inDirectory:named:to:inDirectory:overItem:) async throws -> FSFileName`
- `enumerateDirectory(_ dir: FSItem, startingAt cookie: FSDirectoryCookie, verifier: FSDirectoryVerifier,
  attributes: FSItemGetAttributesRequest?, packer: FSDirectoryEntryPacker) async throws -> FSDirectoryVerifier`
  - паковать каждую запись: `packer.packEntry(name:itemType:itemID:nextCookie:attributes:)`.
  - cookie/verifier — **наши** opaque-значения (стартовые: `FSDirectoryCookieInitial`/`FSDirectoryVerifierInitial`).
  - если `attributes == nil` — добавить `"."` и `".."`.
- `readSymbolicLink`, `createSymbolicLink`, `createLink` — кинуть `ENOTSUP` (символ/хардлинки не поддерживаем).

### `FSVolume.PathConfOperations` (обязателен, в составе Operations)
`maximumLinkCount`, `maximumNameLength`, `restrictsOwnershipChanges`, `truncatesLongNames`,
`maximumXattrSize`, `maximumFileSize` и т.п. — отдать разумные дефолты.

### `FSVolume.ReadWriteOperations` (для чтения/записи файлов)
- `read(from item: FSItem, at offset: off_t, length: size_t, into buffer: FSMutableFileDataBuffer)
  async throws -> size_t` — наш `RangeBlockReader.read(...)` → `buffer.withUnsafeMutableBytes`.
  Если offset за концом — вернуть `0` без ошибки.
- `write(contents: Data, to item: FSItem, at offset: off_t) async throws -> size_t` — буферизуем,
  реальный upload — на закрытии (см. семантику записи [[macos-drive]]).

### Прочие опциональные протоколы
`FSVolume.OpenCloseOperations` (open/close — удобная точка для триггера upload на close),
`FSVolume.RenameOperations`, `FSVolume.XattrOperations`, `FSVolume.ItemDeactivation`,
`FSVolume.PreallocateOperations`, `FSVolume.AccessCheckOperations`.

## FSItem (узел) и атрибуты

- `FSItem` — базовый класс (NSObject). **Подклассить** для своих узлов: хранить `entryId`/`fileId`/
  `directoryId`, тип, кэш атрибутов листинга.
- `FSItemType`: `.unknown/.file/.directory/.symlink` (+ fifo/char/block/socket).
- `FSItemAttributes` (читается из листинга): `uid`, `gid`, `mode`, `type`, `linkCount`, `flags`,
  `size`, `allocSize`, `fileID: FSItemID`, `parentID`, `modifyTime/changeTime/accessTime/birthTime` (`timespec`).
- `FSItemGetAttributesRequest.wantedAttributes: FSItemAttribute` (битовая маска — что заполнять).
- `FSItemSetAttributesRequest: FSItemAttributes` + `consumedAttributes` (что реально применили).
- `FSItemID` — `UInt64` (нужен стабильный per-узел id; маппим из cloud entry/file id).
- `FSFileName(string:)` / `init(cString:)` / `init(bytes:)`.

## Маппинг cloud → FSKit (детализация [[macos-drive]])

| FSKit | BarkCloudKit |
|---|---|
| `activate` → root FSItem | синтетический корень (directoryId = root) |
| `enumerateDirectory` | `CloudRepository.listDirectory(dirID)` → pack каждую запись |
| `attributes(of:)` | из кэша листинга (size/mtime/type) |
| `lookupItem(named:)` | поиск в листинге родителя по имени |
| `read(from:at:length:into:)` | `RangeBlockReader.read(fileID:fileLength:offset:length:)` |
| `write` + close | буфер → `getUploadURL` → POST /web/upload → `attachFile` |
| `createItem(.directory)` | `createDirectory` |
| `createItem(.file)` | отложенный upload (на close) |
| `removeItem`/reclaim | `batchDeleteFileEntries`/`deleteDirectory` |
| `renameItem` | `rename*`/`move*` Directory/FileEntry |
| `volumeStatistics` | `FileTransferService.storageInfo()` (used/limit) |

## Конфиг расширения (Info.plist / entitlements)

- Тип расширения: **`com.apple.fskit.fsmodule`** (NSExtensionPointIdentifier).
- Entitlement: `com.apple.developer.fskit.fsmodule`; client networking; App Group + keychain-access-group
  (общие с контейнер-app — нужен **Team ID** в id App Group: `group.<TeamID>.com.barkfluff.BarkCloud…`).
- Включение тома: System Settings → General → Login Items & Extensions → File System Extensions
  (пользователь включает вручную — FSKit требует явного согласия).
