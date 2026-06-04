# BarkCloud Drive (macOS) — контейнер-app + File Provider-расширение

Нативный macOS-клиент: облако BarkCloud как **папка в Finder** через
**NSFileProviderReplicatedExtension**. Состоит из двух таргетов в
`BarkCloudDrive.xcodeproj`:

- **`BarkCloudDrive`** — контейнер-приложение (SwiftUI + menu-bar): server
  setup, логин, дашборд, настройки, подключение/отключение домена.
- **`BarkCloudFS`** — File Provider-расширение (`com.apple.fileprovider-nonui`):
  реализует `NSFileProviderReplicatedExtension`, маппит операции File Provider
  на облако через пакет `BarkCloudKit`.

Карта реализации — `Obsidian/BarkCloudVault/modules/macos-drive.md`.
Пошаговый план — `Mac/PLAN.md`.

## Сборка (компиляция)

```bash
cd Mac/BarkCloudDrive
xcodebuild build -scheme BarkCloudFS    -configuration Debug CODE_SIGNING_ALLOWED=NO
xcodebuild build -scheme BarkCloudDrive -configuration Debug CODE_SIGNING_ALLOWED=NO
```

Обе схемы должны собираться зелёными. `BarkCloudKit` подтягивается как
локальный SwiftPM-пакет (`../BarkCloudKit`).

## Настройка для запуска

В отличие от FSKit, File Provider **не требует платного Apple Developer**
— достаточно Personal team (бесплатной). Но подпись нужна:

1. **Team ID.** В Xcode выбрать команду подписи для обоих таргетов (Signing
   & Capabilities → Automatically manage signing). Entitlements уже используют
   `$(TeamIdentifierPrefix)` / `$(AppIdentifierPrefix)` для App Group
   `group.<TeamID>.com.barkfluff.BarkCloud` и keychain-access-group.
   - ⚠️ **Сверить App Group id** в `BarkCloudKit/Sources/.../Networking/BarkCloudAppGroup.swift`
     (macOS-ветка) — заменить на `group.<TeamID>.com.barkfluff.BarkCloud`,
     чтобы расширение и app читали один App Group.
2. Запустить контейнер-app, ввести адрес сервера и залогиниться (токены →
   Keychain, общий с расширением).

## Подключение домена

В отличие от FSKit (где нужно было включать расширение в System Settings →
File System Extensions и монтировать через `mount`), File Provider не
требует ручного включения системой:

1. Запустить **BarkCloud Drive.app**, залогиниться.
2. В дашборде нажать **«Подключить»** — `NSFileProviderManager.add(domain:)`
   регистрирует домен в системе.
3. Папка **BarkCloud** появляется:
   - в боковой панели **Locations** в Finder;
   - в `~/Library/CloudStorage/BarkCloud/`.
4. Кнопка **«Открыть в Finder»** — `NSWorkspace.activateFileViewerSelecting`
   по `NSFileProviderManager.getUserVisibleURL(.rootContainer)`.

## Что проверить при первом подключении

- Листинг корня и подпапок в Finder.
- Открытие файла → системный демон `fileproviderd` вызывает
  `fetchContents`, расширение качает блоб через `tempDownloadURLs` →
  `transfer.download`, файл материализуется и открывается ассоциированным
  приложением.
- Создание папки / копирование файла в папку BarkCloud → `createItem`
  (mkdir / uploadFile + attachFile).
- Переименование / перемещение → `modifyItem` (rename/move).
- Удаление → `deleteItem` (`deleteFileEntry` / `deleteDirectory`).
- Перезапись содержимого файла → `modifyItem` с `.contents`: блоб
  удаляется (`deleteFileEntry`) и загружается новый
  (`uploadFile` + `attachFile`), `entryID`/identifier меняется,
  fileproviderd подменяет.

## Релиз (Этап 3)

Сборка дистрибутива — `scripts/build_release.sh` (archive → .dmg/.pkg →
подпись Developer ID → нотаризация → staple). Нужен платный Apple Developer
аккаунт и сертификаты Developer ID в Keychain:

```bash
TEAM_ID=ABCDE12345 \
SIGN_APP="Developer ID Application: <Team> (ABCDE12345)" \
SIGN_PKG="Developer ID Installer: <Team> (ABCDE12345)" \
NOTARY_PROFILE=barkcloud-notary \
scripts/build_release.sh
```

## Осталось (рантайм, после проверки на устройстве)

- Эмпирическая проверка read/write семантики (включая cache hit/miss после
  рестарта расширения).
- Persistent cache в `BarkCloudItemCache` (если cache-miss в пин/recents
  окажется проблемой — см. `Mac/PLAN.md` «Риски»).
- Incremental sync (`enumerateChanges` + бэкенд-стрим изменений) — для пуш-
  обновлений с других клиентов.

Локализация RU/EN/DE и аватар профиля — **готовы**.
