# Android — виджеты

Parent: [[Android/AndroidApp]]
See also: [[Backend/Files]] · [[Android/Authentication]]

## Назначение

Три виджета домашнего экрана: хранилище, корзина и фото из выбранного альбома. Виджеты работают на RemoteViews и не ходят в сеть сами: данные обновляет фоновый воркер в процессе приложения (с его сессией), а виджеты читают локальные снимки. Цвета — Material You (системная палитра Android 12+) с брендовым фолбэком для Android 11.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/widgets/WidgetRefresh.kt` | `WidgetRefreshWorker`, `WidgetRefresh` | Периодическое и разовое обновление, очистка при выходе |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/widgets/StorageWidgetProvider.kt`, `StorageWidgetBridge.kt` | `StorageWidgetProvider`, `StorageWidgetBridge` | Виджет использования хранилища и его снимок |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/widgets/TrashWidgetProvider.kt`, `TrashWidgetBridge.kt`, `TrashDeadline.kt` | `TrashWidgetProvider`, `TrashWidgetBridge`, `trashDeadline` | Корзина: число объектов и срок авто-удаления |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/widgets/PhotoWidgetProvider.kt`, `PhotoWidgetStore.kt`, `PhotoWidgetConfigActivity.kt` | `PhotoWidgetProvider`, `PhotoWidgetStore`, `PhotoWidgetConfigActivity` | Фото из альбома: выбор альбома, кадры в `filesDir`, ротация |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/SessionManager.kt` | `SessionManager.resetLocalState` | Очистка всех виджетов при выходе из аккаунта |
| `Android/BarkCloud.Android/app/src/main/res/values-v31/widget_colors.xml`, `values/widget_colors.xml` | `widget_*` | Цвета: системная палитра и фолбэк |

## Потоки и контракты

- Источники данных: `FileTransferService.storageInfo` (использование хранилища), `CloudRepository.trashSummary` (`GetTrashSummary`: число записей и `oldest_purge_at`), `AlbumRepository.listItems` (кадры фото-виджета, только фото).
- Воркер (`WidgetRefreshWorker`) запускается раз в 15 мин при наличии сети, а также разово при добавлении или обновлении виджета и при открытии приложения (`ON_START`, если виджеты есть). Без действующего refresh-токена он очищает снимки и кадры.
- Корзина хранит абсолютный срок `purgeAt`, а обратный отсчёт считается при каждом рендере; RemoteViews сами не обновляются.
- Хранилище: бэкенд отдаёт `LimitBytes = null` как 0 (без квоты). Такой снимок показывает только занятый объём без процента; «есть данные» определяется успешным снимком (`updatedAt > 0`), а не лимитом.
- Фото-виджет скачивает до 12 уменьшенных кадров через Coil приложения (с авторизацией), пишет их в `filesDir/photo_widgets/<widgetId>` и переключает кадр при каждом проходе воркера. Набор перекачивается, если старше 6 часов или альбом выбран заново. Сетевой сбой не стирает уже показанные кадры.
- Выход из аккаунта (`SessionManager.resetLocalState`) очищает снимки всех виджетов, кадры и выбор альбомов.
- Нажатия открывают `barkcloud://settings`, `barkcloud://trash` или `barkcloud://albums` (обрабатываются в `MainScreen`). Фото-виджет без альбома открывает `PhotoWidgetConfigActivity`; у каждого виджета свой requestCode, так как extras не участвуют в сравнении PendingIntent. Виджет фото объявляет `reconfigurable`.

## Ограничения

- Частота смены фото и обновления данных ограничена минимальным периодом WorkManager (15 мин); Doze может её сдвигать.
- Кадры передаются в RemoteViews как битмапы, поэтому они уменьшены до 400 px.
- Не решено: фото из скрытых от галереи папок попадают в альбом и в виджет так же, как в просмотре альбома. Серверный `ListAlbumItems` такой фильтр не применяет.
- Не решено: показывать ли фото на домашнем экране при включённой блокировке приложения.
