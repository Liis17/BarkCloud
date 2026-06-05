Parent: [[ios-app]]

# iOS — Widgets (план расширения)

> Статус: **реализовано** (ветка `claude/ios-widget-options-duYcN`, #2/#4/#5/#6).
> Создано 2026-06-05. ⚠️ Требует сборки/прогона на Mac — в CI-окружении нет Xcode.
> Расположение кода виджетов: `Ios/BarkCloud/BarkCloud/BarkCloudWidgets/`,
> мосты данных: `Ios/BarkCloud/BarkCloud/BarkCloud/Networking/*WidgetBridge.swift`.

## Что было до расширения

- **`StorageWidget`** — квота облака (`.systemSmall`/`.systemMedium`). Данные кладёт
  main app через `StorageWidgetBridge` в App Group `group.com.barkfluff.BarkCloud`,
  виджет только читает. Кнопка обновления — `RefreshStorageIntent` (поднимает gRPC
  прямо в процессе виджета).
- **`UploadLiveActivity`** — Live Activity фоновой загрузки (см. [[ios-background-upload]]).

Базовые паттерны, которые переиспользуются всеми новыми виджетами:
1. **Мост** `*WidgetBridge.update(...)` → App Group `UserDefaults` + `WidgetCenter.reloadTimelines(ofKind:)`.
2. **Интерактивный фетч** — `AppIntent` с `openAppWhenRun = false`, поднимает временный
   `GrpcManager`/`FileTransferService` в процессе виджета (образец — `RefreshStorageIntent`).

## Новые виджеты (план #2/#4/#5/#6)

### Фаза 0 — Deep link (пререквизит #2/#4/#5)
- Схема `barkcloud://` в `Info.plist` (`CFBundleURLTypes`).
- `App/DeepLink.swift`: `enum DeepLink { albums, trash, vault, media(id) }` + `init?(url:)`.
- `AppEnvironment.pendingDeepLink`; `.onOpenURL` в `RootView` пишет в него.
- `MainScreen` читает и переключает `selection`; для `.vault` → таб `.settings` + push `VaultScreen`.
- Виджеты задают `.widgetURL(...)`/`Link`.

### #6 — Storage на Lock Screen + Control Center (готово первым)
- В `StorageWidget` добавлены `.accessoryCircular/.accessoryRectangular/.accessoryInline`.
- `StorageControl` (`ControlWidget`, iOS 18) — кнопка в Пункте управления, переиспользует
  `RefreshStorageIntent`. Данных новых не требует.

### #5 — Корзина
- `TrashWidgetBridge.update(count:, nearestPurgeAt:)` из `TrashViewModel.reload()`.
  Дедлайн авто-удаления берётся из `TrashItem.purgeAt` (уже приходит с бэкенда).
- `RefreshTrashIntent` (`cloud.listTrash`) для самостоятельного обновления.
- `TrashWidget` — `.accessoryRectangular` + `.systemSmall`, тап → `barkcloud://trash`.

### #4 — Сейф (privacy-sensitive)
- `VaultWidgetBridge.update(count:)` из `VaultStore.persist()`.
- **Opt-in**: настройка `vault_widget_enabled` (по умолчанию OFF) — при выключенной
  число не светится, рисуется только замок.
- `VaultWidget` — `.accessoryCircular` + `.systemSmall`, тап → `barkcloud://vault`.

### #2 — Недавние фото (самый дорогой)
- `RecentMediaWidgetBridge` — даунскейлит превью последних ~8 облачных медиа в
  shared-контейнер (`recent_widget/*.jpg`) + манифест в App Group. Чистится в
  `resetLocalState()`.
- `RefreshRecentIntent` для обновления виджетом.
- `RecentMediaWidget` — `.systemMedium`/`.systemLarge`, тап по фото → `barkcloud://media/<id>`.

## Открытые продуктовые вопросы
1. #4: показывать ли счётчик сейфа по умолчанию (решено: **скрывать**, opt-in).
2. #2: лимит кэша превью — по числу (8) или по размеру.
