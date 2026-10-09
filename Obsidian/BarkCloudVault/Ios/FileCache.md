# iOS — Файловый кеш

Parent: [[Ios/IosApp]]

## Назначение

`FileCacheService` кэширует оригиналы и изображения облачных файлов на диске, чтобы повторно использовать скачанные данные при просмотре медиа и файлов. Этот кеш отделён от временных файлов и очереди фоновой загрузки.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Ios/BarkCloud/BarkCloud/Data/Cache/FileCacheService.swift` | `FileCacheService` | Загрузка, выдача и обслуживание дисковых записей |
| `Ios/BarkCloud/BarkCloud/Data/Cache/CachedFileEntry.swift` | `CachedFileEntry` | SwiftData-метаданные одной записи |
| `Ios/BarkCloud/BarkCloud/Data/Cache/CacheVariant.swift` | `CacheVariant` | Вариант файла в составном ключе |
| `Ios/BarkCloud/BarkCloud/Data/Cache/FileCacheSettings.swift` | `FileCacheSettings` | Лимит размера и срок хранения |
| `Ios/BarkCloud/BarkCloud/Features/Shared/RemoteImage.swift`, `Ios/BarkCloud/BarkCloud/Features/Shared/FilePreviewController.swift`, `Ios/BarkCloud/BarkCloud/Features/Shared/MediaPagerScreen.swift` | использование кеша | Изображения и QuickLook-оригиналы |
| `Ios/BarkCloud/BarkCloud/Features/Settings/CacheSettingsScreen.swift`, `Ios/BarkCloud/BarkCloud/Features/Settings/CacheSettingsViewModel.swift` | настройки кеша | Просмотр размера и очистка |

## Публичные контракты

| Контракт | Поведение |
|---|---|
| Варианты | `original`, `preview(width:)`, `previewCover`, `avatar`, `avatarPreview`; пара `fileId + variant` задаёт запись |
| `loadFile(fileId:variant:urlResolver:)` | Возвращает локальный URL, при отсутствии загружает удалённый файл |
| `loadData(fileId:variant:sourceURL:)` | Возвращает байты из кеша или загружает и сохраняет их |
| Обслуживание | `totalSize`, `entryCount`, `evictStale`, `enforceSizeLimit`, `clearAll` |

## Зависимости и взаимодействия

- Метаданные SwiftData находятся в `Application Support/BarkCloudCache.sqlite`; байты — в `Library/Caches/BarkCloudFiles/<fileId>/`.
- `RemoteImage` использует дисковый кеш при наличии `fileId` и дополнительно держит декодированные изображения в `NSCache`. Просмотр оригинала в QuickLook использует `FileCacheService.loadFile`.
- Экран кеша в настройках показывает размер и число записей, позволяет задать лимит и срок очистки, удалить устаревшие данные или очистить кеш целиком.

## Ограничения и важные детали

- Лимит по умолчанию — 5 ГиБ; доступны значения 1, 2, 5, 10 и 20 ГиБ. При превышении удаляются наименее недавно использованные записи по `lastAccessAt`.
- Срок автоочистки по умолчанию — 7 дней без обращений; можно отключить. Стартовая очистка запускается не чаще раза в сутки.
- Если запись метаданных есть, но файла на диске нет, запись удаляется и данные запрашиваются заново. Явная очистка удаляет записи SwiftData и каталог кеша.
