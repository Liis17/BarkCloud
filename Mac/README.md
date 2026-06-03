# BarkCloud для macOS — виртуальный диск (FSKit)

Нативный macOS-клиент: монтирует облако BarkCloud как **том в Finder** (боковая панель +
рабочий стол) с подкачкой содержимого по запросу. Аналог Windows-клиента `Drive/`
(`X:` через Dokany), см. [Drive/README.md](../Drive/README.md) и заметку памяти
`Obsidian/BarkCloudVault/modules/macos-drive.md`.

➡️ **Пошаговый план для выполнения на Mac: [PLAN.md](PLAN.md)** (этапы 0→3 с проверками).

## Решения

- **ФС: FSKit** (`FSUnaryFileSystem`, macOS 15.4+) — нативно, без kext, нотаризуется.
- **Язык: Swift**, переиспользование сетевого слоя iOS через пакет `BarkCloudKit`.
- **Полный паритет** с Windows: read-write, Range-чтение, батч-удаление, mkdir/move/rename,
  автозапуск, инсталлятор.

## Соответствие Windows → macOS

| Windows (`Drive/`) | macOS (`Mac/`) |
|---|---|
| `BarkCloud.Drive.Engine` (Dokany ФС) | `BarkCloudFS.appex` — FSKit-расширение |
| `BarkCloud.Drive.App` (WPF + трей) | `BarkCloud Drive.app` — SwiftUI + menu-bar |
| Contracts + named-pipe IPC | App Group + shared Keychain access-group |
| DPAPI `refresh.bin` | Keychain (`SessionStore`) |
| Dokany driver | FSKit (встроен) + включение расширения в System Settings |
| Registry `Run` | `SMAppService` (Login Item) |

На macOS отдельного «движка-процесса» нет: **FSKit сам поднимает процесс расширения**.
Контейнер-app пишет конфиг/токены в общее хранилище и инициирует mount/unmount; refresh-токеном
и авторефрешем владеет расширение (живёт, пока том примонтирован).

## Структура (план)

- `BarkCloudKit/` — общий SwiftPM-пакет (сеть + proto + Range-ридер + батч-удаление). **← Этап 0 начат**
- `BarkCloudDrive/` — Xcode-проект: контейнер-app + таргет расширения `BarkCloudFS`. *(Этапы 1–3)*

## Статус

- **Этап 0 (общий пакет):** заготовка — `BarkCloudKit/` создан (новый код + манифест).
  Перенос файлов из iOS, правка `.xcodeproj` и сборка — на Mac. См. `BarkCloudKit/README.md`.
- **Этапы 1–3 (FSKit-расширение, app, упаковка):** требуют Xcode/macOS, ещё не начаты.

> Всё в этом каталоге собирается и проверяется только на **Mac (Xcode 16+, macOS 15.4+)**.
