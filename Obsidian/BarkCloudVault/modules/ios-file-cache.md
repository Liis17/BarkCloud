# iOS — Файловый кеш

Parent: [[ios-app]]

## Назначение

Постоянный дисковый кеш облачных файлов (превью, обложки, аватары, оригиналы).
При наличии файла в кеше отдаёт его **без сети**; при отсутствии — скачивает через
`InsecureHTTP.session` (self-signed TLS), сохраняет на диск и обновляет
`lastAccessAt`. Решает проблему: раньше каждое отображение превью/оригинала
дотягивало байты с сервера (`RemoteImage` держал картинку только в `NSCache`,
`RemoteFilePreviewScreen` каждый раз заново вызывал `GetTempDownloadUrl` + download).

## Расположение

`Ios/BarkCloud/BarkCloud/Data/Cache/`

| Файл | Содержимое |
|---|---|
| `CacheVariant.swift` | `enum CacheVariant { original, preview(width:), previewCover, avatar, avatarPreview }` + `storageKey` (например, `"preview-512"`) — часть ключа БД и базовое имя файла. |
| `CachedFileEntry.swift` | SwiftData `@Model`: `key` (`.unique`), `fileId`, `variant`, `sourceURL?`, `relativePath`, `sizeBytes: Int64`, `lastAccessAt`, `createdAt`. Хелпер `key(fileId:variant:)`. |
| `FileCacheService.swift` | `actor FileCacheService` — единая точка доступа. |
| `FileCacheSettings.swift` | Обёртка над `UserDefaults`: `maxCacheBytes` (дефолт 5 ГБ), `staleMaxAge` (порог автоочистки по возрасту, дефолт 7 дней, `nil`/`0` = «Никогда») и `lastSweepAt`. |

> Примечание: в этой же папке `AutoUploadSettings.swift` относится к отдельной
> фиче авто-загрузки/бэкапа (`[[ios-app]]` → BackupManager), не к кешу.

## Хранилище

- **Метаданные** — SwiftData, БД `Application Support/BarkCloudCache.sqlite`
  (`AppEnvironment.makeCacheContainer()`; при сбое открытия — фолбэк на in-memory).
- **Байты** — `Library/Caches/BarkCloudFiles/{fileId}/{variant.storageKey}.{ext}`.
  `Library/Caches/` помечен как evictable и не бэкапится в iCloud — подходит для кеша.
- **Ключ записи** — `"{fileId}::{variant.storageKey}"`. fileId — натуральный домен;
  вариант — измерение «оригинал / превью‑128 / превью‑512 / cover / avatar».
- **Превью переменной ширины**: `MediaAsset.preview(preferredWidth:)` возвращает URL
  **и фактическую ширину** выбранного превью; вариант собирается как
  `.preview(width: фактическая)`, поэтому один физический файл превью получает один
  ключ независимо от запрошенной ширины (нет дублей).

## Публичное API `FileCacheService`

```swift
init(modelContainer: ModelContainer, settings: FileCacheSettings, http: URLSession)

func loadFile(fileId:variant:urlResolver:) async throws -> URL   // оригиналы (QuickLook)
func loadData(fileId:variant:sourceURL:) async throws -> Data    // превью/аватары
func totalSize() -> Int64
func entryCount() -> Int
func evictStale(olderThan: TimeInterval = 7*24*3600)
func enforceSizeLimit()        // LRU по lastAccessAt, пока size > maxCacheBytes
func clearAll()                // полная очистка (sign-out / кнопка)
func runStartupSweepIfNeeded() // раз в неделю при старте
```

- `loadFile`/`loadData` ищут запись по ключу. Если файл на диске есть → обновляют
  `lastAccessAt` и возвращают. Если файл пропал (система очистила Caches) — удаляют
  «осиротевшую» запись и пере-скачивают.
- Оригиналы используют `urlResolver` (closure с `GetTempDownloadUrl`), так как
  временные signed-ссылки **не подходят как ключ** — ключ = `(fileId, .original)`,
  ссылка тянется только при cache-miss.
- Расширение файла определяется из `response` (suggestedFilename → mimeType → URL).

## Политика eviction

Два механизма работают совместно:
- **Возраст**: `runStartupSweepIfNeeded` при старте, но не чаще раза в сутки
  (`lastSweepAt`), зовёт `evictStale(olderThan: staleMaxAge)` + `enforceSizeLimit()`,
  затем обновляет `lastSweepAt`. Порог `staleMaxAge` настраивается в UI (1/7/30 дней
  или «Никогда»); при «Никогда» (`nil`) возрастная очистка пропускается, но лимит по
  размеру всё равно применяется.
- **Размер**: после каждого успешного сохранения и в стартовом sweep —
  `enforceSizeLimit()` LRU по `lastAccessAt`, пока не уложимся в `maxCacheBytes`.

## Точки интеграции (UI)

- `Features/Shared/RemoteImage.swift` — `RemoteImage(fileId:variant:url:)` (cache-aware)
  + legacy `RemoteImage(url:)`. `FallbackRemoteImage(fileId:urls:)` для аватара
  (первый URL → `.avatarPreview`, второй → `.avatar`). Доступ к кешу — через
  `@Environment(AppEnvironment.self)`.
- `Features/Shared/MediaThumb.swift` — принимает `fileId` + `previewWidth`.
- `Features/Shared/FilePreviewController.swift` — `RemoteFilePreviewScreen` принимает
  `cache: FileCacheService`, оригинал грузится через `loadFile(.original)`.
- Callsites: `MediaGridScreen`, `AlbumDetailScreen` (через `MediaThumb`),
  `AlbumsGridScreen` (`.previewCover`), `CloudBrowserScreen` / `TrashScreen`
  (`.preview` ширины 128), `SettingsScreen` (аватар, `ProfileViewModel.profilePictureFileID`).
- `Features/Settings/CacheSettingsScreen.swift` + `CacheSettingsViewModel.swift` —
  раздел «Кеш»: сегментированный бар хранилища устройства (другое/кеш/свободно — ёмкость
  тома через `volumeAvailableCapacityForImportantUsage`/`volumeTotalCapacity`), размер
  и число записей, селектор лимита (1/2/5/10/20 ГБ), период автоочистки (1/7/30 дней
  или «Никогда» → `staleMaxAge`), кнопки «Очистить устаревшее» / «Очистить весь кеш».
- `App/AppEnvironment.swift` — `fileCache`/`fileCacheSettings`, стартовый sweep в
  `init()`, `fileCache.clearAll()` в `resetLocalState()` (кеш строго пользовательский).

## Тесты

`Ios/BarkCloud/BarkCloudTests/FileCacheServiceTests.swift` — отдельный unit-test
таргет `BarkCloudTests` (host-based, `@testable import BarkCloud`). Покрывает
`enforceSizeLimit` (порядок LRU-вытеснения) и `runStartupSweepIfNeeded` (логика
порога по `lastSweepAt`): записи вставляются прямо в in-memory `ModelContainer`.

```bash
cd Ios/BarkCloud
xcodebuild test -project BarkCloud.xcodeproj -scheme BarkCloud \
  -destination 'platform=iOS Simulator,name=iPhone 17'
```
