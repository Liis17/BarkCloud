# План реализации macOS-клиента (выполнять на Mac)

Пошаговый чеклист для разработки нативного macOS-клиента BarkCloud (папка
облака в Finder через **NSFileProviderReplicatedExtension**). Обзор и решения —
[README.md](README.md); память проекта — `Obsidian/BarkCloudVault/modules/macos-drive.md`;
референс — Windows-клиент `Drive/`.

> **Требования среды:** macOS 15.4+, Xcode 16+, Apple Developer-аккаунт
> (для рантайма — даже Personal team, в отличие от FSKit; для нотаризации/
> распространения — платный с Developer ID). Инструменты proto:
> `brew install protobuf swift-protobuf grpc-swift`.

Легенда проверки: `✅ verify:` — как убедиться, что шаг выполнен.

---

## Этап 0 — Общий пакет `BarkCloudKit` + миграция iOS

**СТАТУС: ВЫПОЛНЕН.** Сетевой слой iOS перенесён в SwiftPM-пакет
`Mac/BarkCloudKit/` — единый источник правды для iOS, контейнер-app и
File Provider-расширения. `swift build` + `xcodebuild` iOS-таргетов —
зелёные.

---

## Этап 1 — File Provider-расширение `BarkCloudFS.appex`

**СТАТУС: КОМПИЛИРУЕТСЯ.** `Mac/BarkCloudDrive/BarkCloudFS/` — app-extension
типа `com.apple.fileprovider-nonui`.

Файлы:
- `BarkCloudFileProvider.swift` — `NSFileProviderReplicatedExtension`,
  `loadServices()` lazy (@MainActor, gRPC + Keychain из App Group),
  `item(for:)`, `enumerator(for:)`, `fetchContents`, `createItem`,
  `modifyItem`, `deleteItem`.
- `BarkCloudFileProviderItem.swift` — `NSFileProviderItem` (директория /
  файл / root), `itemIdentifier` = `"d:<dirID>"` / `"f:<entryID>"` /
  `.rootContainer`, `itemVersion(contentVersion: fileID,
  metadataVersion: name+parent+modified)`, capabilities per-type.
- `BarkCloudItemCache.swift` — `actor` cache `identifier → CloudDirectory/
  CloudFileEntry`, заполняется при enumerate. На cache miss — `.noSuchItem`,
  fileproviderd перезапросит листинг родителя.
- `BarkCloudEnumerator.swift` — per-container enumerator + EmptyEnumerator
  (working-set/trash) + PendingEnumerator (резолв подпапки из actor-кэша,
  т.к. `enumerator(for:)` синхронный).

**Семантика записи** (наследуется от Windows): блобы иммутабельны → правка
существующего файла = `deleteFileEntry` + `uploadFile` + `attachFile` как
новый (новый `entryID`/identifier, fileproviderd подменяет).

**Сессия в расширении:** `loadServices()` поднимает `SessionStore`
(Keychain) + `GrpcManager` (адрес из App Group UserDefaults) + проактивный
авторефреш `CreateToken` на каждом запросе.

**Entitlements расширения:** app-sandbox, network, App Group, keychain-access-group
— общие с app. **БЕЗ** `com.apple.developer.fskit.fsmodule`.

**Info.plist расширения:** `NSExtension.NSExtensionPointIdentifier =
com.apple.fileprovider-nonui`, `NSExtensionPrincipalClass = ...BarkCloudFileProvider`,
`NSExtensionFileProviderSupportsEnumeration = YES`.

**✅ verify Этап 1:** `xcodebuild build -scheme BarkCloudFS
CODE_SIGNING_ALLOWED=NO` — зелёный. После регистрации домена (Этап 2)
проверить листинг в Finder, скачивание файла (cache в
`~/Library/Containers/com.barkfluff.BarkCloud.Drive.FileProvider/Data`),
mkdir/rename/move/delete, контент-edit (delete+reupload).

---

## Этап 2 — Контейнер-приложение `BarkCloud Drive.app`

**СТАТУС: КОМПИЛИРУЕТСЯ.** `Mac/BarkCloudDrive/BarkCloudDrive/`. SwiftUI +
menu-bar (`MenuBarExtra`), переиспользует `BarkCloudKit`.

- `AppModel` — @Observable сервис-контейнер (Grpc/Session/Auth/User/Transfer)
  + фазы serverSetup→login→dashboard.
- `ServerSetupView`, `LoginView` (auth+OTP), `DashboardView`, `SettingsView`,
  `MenuBarView`.
- **`FileProviderDomainManager`** (бывший `MountManager`): `refreshState()`,
  `enable()` (`NSFileProviderManager.add(domain:)`), `disable()` (`remove`),
  `revealInFinder()` (через `getUserVisibleURL(.rootContainer)` +
  `NSWorkspace.activateFileViewerSelecting`).
- Локализация RU/EN/DE — `Localizable.xcstrings` (29 ключей), аватар —
  `RemoteAvatar` через `InsecureHTTP`.

**Автозапуск:** `SMAppService.mainApp.register()` (Login Item) — в
`SettingsView`.

**✅ verify Этап 2:** полный цикл первого запуска → подключение домена;
закрытие окна не отключает домен; выход не отключает домен (он персистится в
системе до явного `remove`); перезапуск с восстановленной сессией; смена
языка на лету; Login Item стартует app при входе в систему.

---

## Этап 3 — Упаковка и автозапуск

**СТАТУС: скрипт готов** — `Mac/BarkCloudDrive/scripts/build_release.sh`
(archive → export → .dmg/.pkg → подпись Developer ID → `notarytool` →
staple). Параметризован env-переменными (`TEAM_ID`/`SIGN_APP`/`SIGN_PKG`/
`NOTARY_PROFILE`).

> Подпись/нотаризация — на стороне пользователя (платный Apple Developer +
> сертификаты Developer ID в Keychain). Для разработки и проверки на своей
> машине достаточно Personal team подписи.

**✅ verify Этап 3:** установка `.pkg` на чистую систему → онбординг →
подключение домена; `spctl`/`stapler validate` проходят; автозапуск при
входе.

---

## Риски и заметки

- **Persistent cache.** Сейчас `BarkCloudItemCache` — in-memory actor.
  После рестарта fileproviderd кэш пустой; fileproviderd обычно сразу делает
  enumerate корня и переходит вглубь, восстанавливая cache. Если этого
  окажется недостаточно (пин/recents в Finder обращаются к item'у не из
  enumerate-цепочки) — добавить persistent cache в App Group UserDefaults
  или SQLite.
- **`findEntry` после upload.** После `cloud.uploadFile` (= getUploadURL +
  upload + attachFile) бэкенд не возвращает `entryID`, поэтому делаем
  `listDirectory(parentDirID)` и ищем по `fileID + name`. Эпизодически
  может быть гонка — мониторить, при необходимости добавить proto-метод
  «attach и верни entryID».
- **`enumerateChanges` без incremental sync.** Сейчас возвращаем
  `finishEnumeratingChanges(upTo: anchor, moreComing: false)` — это значит
  «никаких изменений», что заставляет fileproviderd периодически делать
  полный `enumerateItems`. Для пуш-обновлений (изменения с других клиентов)
  понадобится бэкенд-стрим изменений (proto-метод) и нормальный
  `currentSyncAnchor`.
- **Device-заголовки обязательны** для Auth (Base64(UTF8); `x-auth-token`
  — сырой); `x-device-id` персистить (на macOS — в App Support/Keychain,
  стабильный per-install).
- **Бэкенд правок не требует.** Те же API, что у Windows-клиента
  (`CloudApi.*`, `FilesApi.*`).
- **Память проекта:** обновлять `Obsidian/BarkCloudVault/modules/macos-drive.md`
  по мере фаз.
