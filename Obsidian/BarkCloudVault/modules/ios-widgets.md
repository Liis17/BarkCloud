Parent: [[ios-app]]

# iOS — Widgets (план расширения)

> Статус: **реализовано** (ветка `claude/ios-widget-options-duYcN`, #2/#4/#5/#6).
> Создано 2026-06-05. ⚠️ Требует сборки/прогона на Mac — в CI-окружении нет Xcode.
> Расположение кода виджетов: `Ios/BarkCloud/BarkCloud/BarkCloudWidgets/`,
> мосты данных: `Ios/BarkCloud/BarkCloud/BarkCloud/Networking/*WidgetBridge.swift`.

## Что было до расширения

- **`StorageWidget`** — заполнение физического диска сервера (`.systemSmall`/`.systemMedium`). Данные кладёт
  main app через `StorageWidgetBridge` в App Group `group.com.barkfluff.BarkCloud`,
  виджет только читает. Snapshot содержит legacy `used/limit` и основной разрез
  `diskTotal/diskOther/diskS3`; свободное считается как `diskTotal - diskOther - diskS3`.
  UI: сегментированный бар (другие данные / S3 / свободно), компактные метрики;
  кнопка обновления — `RefreshStorageIntent` (поднимает gRPC прямо в процессе виджета
  и пишет тот же snapshot).
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
  `RefreshStorageIntent`; показывает процент занятого физического диска.

### #5 — Корзина
- Данные виджета — из нового лёгкого RPC **`CloudApi.GetTrashSummary`** (`COUNT` +
  `MIN(PurgeAt)` по `IsDeleted`): точный счётчик и точная ближайшая дата авто-удаления.
  Список корзины остаётся `DeletedAt desc` (UX не меняли), веб не трогали.
  Клиент: `CloudRepository.trashSummary() -> (count, oldestPurgeAt)`.
- `TrashWidgetBridge.update(count:, oldestPurgeAt:)` из `TrashViewModel.reload()` (best-effort).
- `RefreshTrashIntent` тоже зовёт `trashSummary()`.
- `TrashWidget` — `.systemSmall` + `.accessoryRectangular` + `.accessoryCircular`;
  показывает «Удалятся через N дн.», тап → `barkcloud://trash`.
- Бэкенд: `Features/Cloud/GetTrashSummary/` (command+handler), host —
  `CloudApiService.GetTrashSummary`, storage — `ICloudHierarchyStorage.GetTrashSummary`.

### #4 — Сейф (privacy-sensitive)
- `VaultWidgetBridge.update(count:)` из `VaultStore.persist()`.
- **Opt-in**: настройка `vault_widget_enabled` (по умолчанию OFF) — при выключенной
  число не светится, рисуется только замок.
- `VaultWidget` — `.accessoryCircular` + `.systemSmall`, тап → `barkcloud://vault`.

### #2 — Недавние фото (самый дорогой)
- `RecentMediaWidgetBridge` — даунскейлит превью последних ~8 облачных медиа в
  shared-контейнер (`recent_widget/*.jpg`) + манифест в App Group. Чистится в
  `resetLocalState()`.
- `RecentMediaWidget` — `.systemMedium`/`.systemLarge`. Каждая ячейка — `Link` на
  `barkcloud://media/<id>`; фон виджета — `barkcloud://albums`.
- Раскладка (перерисована 2026-06-11): коллаж равными ячейками на весь виджет —
  medium один ряд (до 4), large два сбалансированных ряда (`top = ceil(n/2)`,
  например 5 фото → 3+2). Ячейка: `Color`-база + `overlay { Image.scaledToFill }`
  + `clipShape` — фото обрезается строго по ячейке. Старый вариант
  (`ZStack { Image.scaledToFill }.aspectRatio(1, .fill)` в `LazyVGrid`) давал
  расползание картинок за пределы ячеек с наездами друг на друга.
- Per-photo навигация: `DeepLink.media(id)` → `MainScreen` ставит `AppEnvironment.pendingMediaID`
  → сетка фото (`MediaGridScreen`, kind .photo) открывает пейджер по id (consume-once,
  мягкий фолбэк — если id не в загруженной странице, остаётся обычная сетка).

## Открытые продуктовые вопросы
1. #4: показывать ли счётчик сейфа по умолчанию (решено: **скрывать**, opt-in).
2. #2: лимит кэша превью — по числу (8) или по размеру.
