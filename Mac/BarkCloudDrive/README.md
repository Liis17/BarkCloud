# BarkCloud Drive (macOS) — контейнер-app + FSKit-расширение

Нативный macOS-клиент: монтирует облако BarkCloud как том в Finder через **FSKit**.
Состоит из двух таргетов в `BarkCloudDrive.xcodeproj`:

- **`BarkCloudDrive`** — контейнер-приложение (SwiftUI + menu-bar): server setup, логин,
  дашборд, настройки, монтаж/размонтаж.
- **`BarkCloudFS`** — FSKit-расширение (`com.apple.fskit.fsmodule`): реализует том
  (`FSVolume`), маппит FS-операции на облако через пакет `BarkCloudKit`.

Карта реализации — `Obsidian/BarkCloudVault/modules/macos-drive.md` и `macos-fskit-api.md`.
Пошаговый план — `Mac/PLAN.md`.

## Сборка (компиляция)

```bash
cd Mac/BarkCloudDrive
xcodebuild build -scheme BarkCloudFS    -configuration Debug CODE_SIGNING_ALLOWED=NO
xcodebuild build -scheme BarkCloudDrive -configuration Debug CODE_SIGNING_ALLOWED=NO
```

Обе схемы должны собираться зелёными. `BarkCloudKit` подтягивается как локальный SwiftPM-пакет
(`../BarkCloudKit`).

## Настройка для запуска (нужен Apple Developer аккаунт)

Компиляция подписи не требует, но **монтирование и работа расширения требуют подписи + entitlements**:

1. **Team ID.** В Xcode выбрать команду подписи для обоих таргетов (Signing & Capabilities →
   Automatically manage signing). Entitlements уже используют `$(TeamIdentifierPrefix)` /
   `$(AppIdentifierPrefix)` для App Group `group.<TeamID>.com.barkfluff.BarkCloud` и
   keychain-access-group — отдельно прописывать Team ID не нужно.
   - ⚠️ **Сверить App Group id с пакетом:** `BarkCloudKit/Sources/.../Networking/BarkCloudAppGroup.swift`
     (macOS-ветка) сейчас `group.com.barkfluff.BarkCloud` — заменить на реальный
     `group.<TeamID>.com.barkfluff.BarkCloud`, чтобы расширение и app читали один App Group.
2. **Entitlement `com.apple.developer.fskit.fsmodule`** требует профиля (платный аккаунт).
3. Запустить контейнер-app, ввести адрес сервера и залогиниться (токены → Keychain, общий с расширением).

## Включение и монтирование (рантайм — не проверено сборкой)

1. **Включить расширение:** System Settings → General → Login Items & Extensions →
   **File System Extensions** → включить «BarkCloud». FSKit требует явного согласия пользователя.
2. **Примонтировать:** кнопка «Примонтировать» в дашборде (или меню в строке состояния).

> ⚠️ **Главный технический риск (см. `MountManager.swift`).** Точный механизм монтирования
> URL-based унарной FSKit-ФС публично не задокументирован: `FSClient` отдаёт только список
> установленных модулей, без mount-API. `MountManager` — best-effort обёртка над `mount`/`umount`
> с типом `BarkCloud` и ресурсом-URL `barkcloud://`. На устройстве, вероятно, потребуется уточнить
> фактическую команду монтирования (и, возможно, непесочный helper, т.к. контейнер-app в App Sandbox).

## Что проверить при первом маунте

- Листинг корня и подпапок в Finder (в логе — `enumerateDirectory`).
- Открытие большого файла → чтение **Range-блоками** (не целиком), см. `RangeBlockReader`.
- Запись/копирование файла → upload на закрытии (`closeItem`), виден после ремаунта.
- Удаление (`deleteFileEntry`/`deleteDirectory`), mkdir, rename/move.
- **Риск к проверке:** `FSItem.Identifier(rawValue:)` для произвольных inode-id — если это
  закрытый enum `{0,1,2}`, узлам нужен другой носитель id.

## Осталось (после рантайм-проверки)

- Локализация RU/EN/DE (сейчас строки RU инлайн).
- Загрузка аватара в дашборде через `InsecureHTTP` (self-signed TLS).
- **Этап 3:** `.pkg`/`.dmg`, подпись Developer ID + нотаризация (`notarytool`), staple, онбординг.
