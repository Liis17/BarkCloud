# iOS — Виджеты

Parent: [[Ios/IosApp]]
See also: [[Ios/BackgroundUpload]]

## Назначение

Таргет `BarkCloudWidgets` содержит виджеты хранилища, корзины, сейфа и недавних облачных фото, Control Center control для хранилища и отображение Live Activity загрузки.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Ios/BarkCloud/BarkCloudWidgets/BarkCloudWidgetsBundle.swift` | `BarkCloudWidgetsBundle` | Состав расширения |
| `Ios/BarkCloud/BarkCloudWidgets/StorageWidget.swift`, `Ios/BarkCloud/BarkCloudWidgets/RefreshStorageIntent.swift`, `Ios/BarkCloud/BarkCloudWidgets/StorageControl.swift` | `StorageWidget`, `RefreshStorageIntent`, `StorageControl` | Использование хранилища и обновление снимка |
| `Ios/BarkCloud/BarkCloudWidgets/TrashWidget.swift`, `Ios/BarkCloud/BarkCloudWidgets/RefreshTrashIntent.swift` | `TrashWidget`, `RefreshTrashIntent` | Корзина и ручное обновление |
| `Ios/BarkCloud/BarkCloudWidgets/VaultWidget.swift` | `VaultWidget` | Быстрый переход в сейф |
| `Ios/BarkCloud/BarkCloudWidgets/RecentMediaWidget.swift` | `RecentMediaWidget` | Недавние облачные фото и видео |
| `Ios/BarkCloud/BarkCloudWidgets/UploadLiveActivity.swift`, `Ios/BarkCloud/Shared/UploadActivityAttributes.swift` | `UploadLiveActivity`, `UploadActivityAttributes` | Отображение фоновых загрузок |
| `Ios/BarkCloud/BarkCloud/Networking/StorageWidgetBridge.swift`, `Ios/BarkCloud/BarkCloud/Networking/TrashWidgetBridge.swift`, `Ios/BarkCloud/BarkCloud/Networking/VaultWidgetBridge.swift`, `Ios/BarkCloud/BarkCloud/Networking/RecentMediaWidgetBridge.swift` | мосты приложения | Запись данных для расширения |
| `Ios/BarkCloud/BarkCloud/App/DeepLink.swift`, `Ios/BarkCloud/BarkCloud/Info.plist` | `DeepLink`, схема `barkcloud` | Обработка переходов из виджетов |

## Публичные контракты

- Тапы открывают `barkcloud://albums`, `barkcloud://trash`, `barkcloud://vault` или `barkcloud://media/<file-id>`; обработка ссылок описана в [[Ios/IosApp]].
- Приложение и расширения используют App Group `group.com.barkfluff.BarkCloud` для снимков и миниатюр. Их формат совместно читают bridges приложения и соответствующие виджеты.
- Виджет сейфа показывает число элементов только при включённой настройке приватности; иначе отображает замок без количества.

## Зависимости и взаимодействия

- Виджеты хранилища и корзины читают сохранённые снимки из App Group. `RefreshStorageIntent` и `RefreshTrashIntent` могут запросить свежие данные у сервера через `BarkCloudKit`.
- `RecentMediaWidgetBridge` записывает манифест и локальные JPEG миниатюры в общий контейнер. Сам виджет не загружает изображения по сети; защищённые элементы сейфа отфильтровываются до публикации миниатюр.
- `UploadLiveActivity` показывает состояние, которое публикует основное приложение; поток загрузки описан в [[Ios/BackgroundUpload]].

## Ограничения и важные детали

- Расширение собирается с минимальной версией iOS 18. Widget timelines читают локальные данные; сетевое обновление запускается явным App Intent.
- Ссылка `barkcloud://media/<file-id>` открывает облачное медиа по идентификатору, полученному из манифеста недавних фото.
