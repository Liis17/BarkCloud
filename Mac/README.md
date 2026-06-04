# BarkCloud для macOS — клиент облака (File Provider)

Нативный macOS-клиент: монтирует облако BarkCloud как **папку в Finder**
(боковая панель Locations + `~/Library/CloudStorage/BarkCloud`) с подкачкой
содержимого по запросу. Реализован через **NSFileProviderReplicatedExtension** —
тот же механизм, что у iCloud Drive, Dropbox, Google Drive (новый клиент),
OneDrive.

➡️ **Пошаговый план для выполнения на Mac: [PLAN.md](PLAN.md)**.

## Решения

- **API: NSFileProviderReplicatedExtension** (macOS 11+) — нативно, не требует
  специальных entitlements, доступно Personal Apple Developer team, нотаризуется
  как обычное приложение, App-Store-совместимо.
- **Язык: Swift**, переиспользование сетевого слоя iOS через пакет
  `BarkCloudKit`.

> **Почему не FSKit:** капабилити `com.apple.developer.fskit.fsmodule` не
> поддерживается Personal team Apple Developer (нужен платный аккаунт).
> File Provider даёт ту же UX (папка облака в Finder с on-demand-материализацией)
> без специальных entitlements. См. `Obsidian/BarkCloudVault/modules/macos-drive.md`.

> **Почему не macFUSE / FUSE-T:** kext / system extension с непривычной
> установкой, не App-Store-совместимо, риски при обновлениях macOS.

## Соответствие Windows → macOS

| Windows (`Drive/`) | macOS (`Mac/`) |
|---|---|
| `BarkCloud.Drive.Engine` (Dokany ФС) | `BarkCloudFS.appex` — NSFileProvider-расширение |
| `BarkCloud.Drive.App` (WPF + трей) | `BarkCloud Drive.app` — SwiftUI + menu-bar |
| Contracts + named-pipe IPC | App Group + shared Keychain access-group |
| DPAPI `refresh.bin` | Keychain (`SessionStore`) |
| Dokany driver | NSFileProvider (встроен) + `NSFileProviderManager.add(domain:)` |
| Registry `Run` | `SMAppService` (Login Item) |

На macOS отдельного «движка-процесса» нет: **системный демон `fileproviderd`
сам поднимает процесс расширения** по запросу. Контейнер-app пишет конфиг/токены
в общее хранилище и регистрирует домен; refresh-токеном и авторефрешем владеет
расширение (живёт, пока fileproviderd удерживает домен).

## Структура

- `BarkCloudKit/` — общий SwiftPM-пакет (сеть + proto). Платформо-независимый
  сетевой слой iOS перенесён сюда — единый источник правды.
- `BarkCloudDrive/` — Xcode-проект:
  - `BarkCloudDrive` (контейнер-app) — SwiftUI + menu-bar, регистрирует домен.
  - `BarkCloudFS` (расширение) — `NSFileProviderReplicatedExtension`, маппит
    операции File Provider на облако через `BarkCloudKit`.

## Статус

- **Стадия A (каркас File Provider):** код компилируется. Тип таргета
  `app-extension` (`com.apple.fileprovider-nonui`), новый Info.plist + entitlements
  без FSKit-капабилити.
- **Стадия B (read-path):** код компилируется. `BarkCloudFileProviderItem`,
  `BarkCloudItemCache`, `BarkCloudEnumerator`, `fetchContents` через
  `tempDownloadURLs` + `download`.
- **Стадия C (write-path):** код компилируется. `createItem` (mkdir / upload+attach),
  `modifyItem` (rename/move; contents — delete+upload, блобы иммутабельны),
  `deleteItem`.
- **Стадия D (контейнер-app):** код компилируется. `MountManager` →
  `FileProviderDomainManager` (`NSFileProviderManager.add(_:)`), Dashboard/MenuBar/
  Settings обновлены.
- **Осталось — только рантайм/устройство:** включить домен из дашборда, эмпирически
  проверить листинг/чтение/запись в Finder, проверить корректность `cache` после
  рестарта расширения. См. `BarkCloudDrive/README.md`.

> Всё в этом каталоге собирается и проверяется только на **Mac (Xcode 16+, macOS 15.4+)**.
