# Plan 07 — iOS: общий доступ (публичные ссылки + шаринг между пользователями)

> Клиент: `Ios/BarkCloud`. **Бэкенд и веб-UI готовы**, proto-стабы перегенерированы. Цель — догнать веб-функционал на iOS: управление публичными ссылками, шаринг конкретным пользователям, входящие шары.

## Анализ (что уже есть на iOS)

**Работает:**
- `CloudRepository.createShare(fileID:name:)` → `ShareLink` (`Data/Cloud/CloudRepository.swift:88-95`).
- `ShareLink` модель + сборка URL: `{webHost}/s/{token}` (`Data/Cloud/CloudModels.swift:153-167`, `Networking/GrpcManager.swift:115`).
- Действия «Создать публичную ссылку» в галерее, медиасетке, альбомах (копирует URL в `UIPasteboard`):
  - `GalleryViewModel.makePublic(asset:)` (`Features/Gallery/GalleryViewModel.swift:212`)
  - `MediaGridViewModel` (`Features/Media/MediaGridViewModel.swift:251`)
  - `AlbumDetailScreen` (`Features/Media/Albums/AlbumDetailScreen.swift:144`)

**В proto после регенерации доступно (но не используется на iOS):**
- `CloudApi.ListMyShares` / `RevokeShare`
- `CloudApi.ShareFileWithUser` / `RevokeUserShare`
- `CloudApi.ListMyOutgoingShares` (с кем поделён файл)
- `CloudApi.ListSharedWithMe` (входящие, курсорная пагинация)
- `CloudApi.GetSharedFileDownloadUrl` (временная ссылка на скачивание грантованного файла)
- `UsersApi.SearchUsers` (поиск получателей)
- `ResolveShareResponse` обогащён `mediaKind/previewURL/imageWidth/Height/fileSize` (не нужно iOS-клиенту, страница `/v/:token` остаётся веб-эксклюзивом — пользователь открывает её в Safari).

## Архитектура

### Навигация

5 табов TabView полны (Gallery/Files/Albums/Trash/Settings). Новый таб «Общий доступ» не лезет без `More`-разворота — это плохо UX. **Решение:** toolbar-кнопка справа сверху в `FilesRootScreen` (иконка `link.circle` или `person.2.circle`) → push к `SharedHubScreen`. Альтернатива (запасной вариант) — пункт «Общий доступ» в верхней секции `SettingsScreen`.

### Источники истины и пагинация

Курсорная пагинация уже есть в проекте (галерея — `MediaGridViewModel`); используем тот же подход: `@Observable final class ...VM`, поля `items: [T]`, `nextCursor*`, `isLoading`, `load()`/`loadMore()` + `.task`/`.onAppear` на последней карточке.

### Действия «отозвать / поделиться» — синхронизация UI

После `revokeShare` / `revokeUserShare` оптимистично убираем из локального массива; `shareFileWithUser` — оптимистично помечаем юзера в SwiftUI-state модалки (как на вебе: галочка «Выдан»). Ошибка → toast + откат.

### Системный Share Sheet vs UIPasteboard

Сейчас `makePublic` кладёт URL в буфер и показывает snackbar. На вебе так же (`navigator.clipboard.writeText`). **Оставляем как есть в MVP**, чтобы UX совпадал; кнопка «Поделиться» (системный `UIActivityViewController`) — опционально позже (можно добавить вторым пунктом в action sheet «Поделиться публичной ссылкой» рядом с «Скопировать»).

---

## Файлы

### Новые

| Путь | Назначение |
|---|---|
| `Data/Cloud/CloudModels.swift` | + поля в `ShareLink` (`createdAt`, `fileID`); новые типы `ShareLinksPage`, `CloudUser`, `OutgoingShare`, `SharedFileEntry`, `SharedWithMePage` |
| `Features/Shared/SharedHubScreen.swift` | Экран «Общий доступ» с `Picker(.segmented)` «Мои публичные» / «Мне доступны» |
| `Features/Shared/MySharesViewModel.swift` | Пагинируемый список своих публичных + revoke |
| `Features/Shared/MySharesListView.swift` | Список карточек: имя файла, URL, переходы, дата, кнопки «Скопировать» / «Отозвать» |
| `Features/Shared/SharedWithMeViewModel.swift` | Пагинируемый список входящих + загрузка |
| `Features/Shared/SharedWithMeListView.swift` | Карточки с превью, имя владельца, дата, кнопка «Скачать» |
| `Features/Shared/ShareWithUserSheet.swift` | Sheet с поиском пользователей + действием «Поделиться» (debounce 300мс, минимум 2 символа) |
| `Features/Shared/OutgoingSharesSheet.swift` (опционально) | «Кто видит этот файл»: список грантов на один файл + кнопка отозвать |

### Изменяемые

| Путь | Что меняем |
|---|---|
| `Data/Cloud/CloudRepository.swift` | + `listMyShares(limit:, cursorCreatedAt:, cursorShareID:)`, `revokeShare(id:)`, `shareFileWithUser(fileID:, recipientUserID:)`, `revokeUserShare(grantID:)`, `listMyOutgoingShares(fileID:)`, `listSharedWithMe(limit:, cursorAt:, cursorGrantID:)`, `getSharedFileDownloadUrl(fileID:)` |
| `Data/Users/UserRepository.swift` | + `searchUsers(query:, limit:)` → `[CloudUser]` |
| `Features/Gallery/GalleryViewModel.swift` | + `shareWithUser(asset:)` (открывает sheet через привязанный state) |
| `Features/Media/MediaGridViewModel.swift` | + `shareWithUser(item:)` |
| `Features/Media/Albums/AlbumDetailScreen.swift` | + пункт меню «Поделиться с пользователем» |
| `Features/Files/UI/FilesRootScreen.swift` | + toolbar-кнопка справа сверху → `NavigationLink(SharedHubScreen())`; + пункт меню для файла «Поделиться с пользователем» |
| `Features/Settings/SettingsScreen.swift` (опционально) | + пункт «Общий доступ» в первой секции (запасной вход на случай узкого Files-toolbar'a) |
| `Resources/Localizable.xcstrings` | Строки: «Общий доступ», «Мои публичные», «Мне доступны», «Поделиться с пользователем», «Отозвать», «Поиск по имени или юзернейму», «Никого не найдено», «Доступ выдан», «От кого», «Скопировать ссылку», «Переходов», «Создана», «Ссылка отозвана», «Не удалось создать ссылку» и т.п. |
| `Obsidian/BarkCloudVault/modules/ios-app.md` | Новая секция «Общий доступ» |

---

## Этапы реализации

Каждый этап заканчивается работающим срезом: можно остановиться после любого из них и иметь полезный функционал в проде.

### Этап 1 — Список моих публичных ссылок + отзыв

**Цель:** пользователь видит все свои публичные ссылки, может скопировать или отозвать.

1. `CloudRepository.listMyShares(...)`, `revokeShare(id:)`.
2. `ShareLink`: добавить поля `createdAt: Date`, `fileID: String` (зеркало `ShareInfo`).
3. `ShareLinksPage { items, nextCursorAt, nextCursorID }`.
4. `MySharesViewModel`: первая страница в `task`, `loadMore()` на видимости предпоследней карточки.
5. `MySharesListView`: имя, обрезанный URL, переходы, дата; кнопки `link.fill` (копировать) и `trash` (отозвать с `.confirmationDialog`).
6. Точка входа: `FilesRootScreen` toolbar → `SharedHubScreen` (этап 1 показывает только таб «Мои публичные»).
7. **Проверка:** создать публичную ссылку из контекстного меню → перейти в «Общий доступ» → ссылка есть с переходами 0 и текущей датой → «Отозвать» → исчезла → открыть URL в Safari → 404.

### Этап 2 — Поделиться с пользователем (поиск + грант)

**Цель:** действие «Поделиться с пользователем» в контекстном меню Файлов/Фото/Видео/Альбомов.

1. `UserRepository.searchUsers(query:, limit:)` (debounce делает UI, сам метод просто await stub.searchUsers).
2. `CloudUser { id: Int64, username, firstName, lastName, avatarURL: URL? }`.
3. `CloudRepository.shareFileWithUser(fileID:, recipientUserID:)`.
4. `ShareWithUserSheet`: `TextField` с debounce 300мс, минимум 2 символа, `List` пользователей с аватаром/именем/`@username` и кнопкой «Поделиться» → после успеха галочка «Выдан». Sheet содержит `.presentationDetents([.medium, .large])`.
5. Привязка через `@State sharedWithItem: ShareWithUserContext?` в `GalleryViewModel`/`MediaGridScreen`/`AlbumDetailScreen`/`FilesRootScreen`. Открытие — `.sheet(item: $sharedWithItem) { ShareWithUserSheet(...) }`.
6. **Проверка:** аккаунт А делится файлом с аккаунтом Б. У Б файл появится в «Мне доступны» (этап 3) — пока без UI, проверка по логам бэкенда / через веб-аккаунт Б.

### Этап 3 — Раздел «Мне доступны» + скачивание

**Цель:** второй таб `SharedHubScreen` с входящими шарами.

1. `CloudRepository.listSharedWithMe(...)`, `getSharedFileDownloadUrl(fileID:)`.
2. `SharedFileEntry { grantID, file: CloudFileEntry, sharedAt: Date, owner: CloudUser }`, `SharedWithMePage`.
3. `SharedWithMeViewModel`: первая страница + `loadMore` по курсору.
4. `SharedWithMeListView`: карточка с превью (через `RemoteImageCache`), имя файла, «От кого», «Когда», кнопка «Скачать».
5. Действие «Скачать»:
   - `getSharedFileDownloadUrl(fileID)` → URL.
   - Загрузка через `BackgroundUploadCoordinator`? Нет — это **скачивание**, не загрузка. Используем `URLSession` (фон-сессия для скачивания — отдельная история); MVP — `URLSession.download(from: url)` на переднем плане → сохранить во временный файл → `UIDocumentPickerViewController(forExporting:)` или `ShareLink`-fallback (отдать пользователю выбор куда сохранить).
6. **Проверка:** Б видит файл от А; тап «Скачать» открывает системную модалку выбора места сохранения.

### Этап 4 — Полировка контекстных меню

**Цель:** «Поделиться с пользователем» доступен из всех мест где есть «Создать публичную ссылку».

1. Добавить пункт во все 4 контекстных меню: `GalleryViewModel.shareWithUser`, `MediaGridViewModel.shareWithUser`, `AlbumDetailScreen` actions, `FilesRootScreen` row menu.
2. Локализовать строки.
3. **Проверка:** во всех 4 местах есть оба пункта; sheet работает одинаково.

### Этап 5 — Управление кому расшарил (опционально)

**Цель:** для конкретного файла увидеть список грантов и отозвать.

1. `CloudRepository.listMyOutgoingShares(fileID:)`.
2. `OutgoingSharesSheet`: загружает список после открытия, рендерит карточки получателей + кнопку «Отозвать» (с `.confirmationDialog`).
3. В sheet «Поделиться с пользователем» наверху показывать «Уже расшарено: N» — тап открывает `OutgoingSharesSheet`.
4. **Проверка:** на файле с 3 грантами видно 3 пользователя; «Отозвать» → исчез; у получателя файл исчез из «Мне доступны».

### Этап 6 — Системный Share Sheet (опционально)

Заменить в `makePublic` копирование URL на `UIActivityViewController` с `[url]` — пользователь выбирает Telegram/Mail/копировать. Сейчас на вебе один вариант (буфер); для iOS это естественнее.

---

## Верификация

### Ручные сценарии

1. **Публичная ссылка + отзыв.** Меню фото → «Создать публичную ссылку» → переход в «Общий доступ» → видна; копирую URL → открываю в Safari (или incognito) → файл скачивается. Отзываю → URL → 404.
2. **Поделиться с пользователем.** Аккаунт А делится файлом с Б → у Б в «Мне доступны» появляется файл с превью и подписью «От: А» → Б скачивает → файл на устройстве.
3. **Пагинация входящих.** У Б 100+ грантов. Скролл подгружает по 60.
4. **Отзыв гранта.** А отзывает грант для Б → у Б файл исчезает (после повторного открытия экрана / pull-to-refresh).
5. **Поиск пользователей.** Минимум 2 символа, debounce, корректная отрисовка аватаров, состояние «Выдан».
6. **Поведение при сетевых ошибках.** offline → понятные toast'ы, кнопка «Повторить» в `EmptyState`.

### Билд

```bash
cd Ios/BarkCloud
xcodebuild -project BarkCloud.xcodeproj \
  -scheme BarkCloud \
  -destination 'platform=iOS Simulator,name=iPhone 17 Pro' \
  build
```

### Тесты (если поднимать `BarkCloudTests`)

- `CloudRepositoryShareTests` — моки на gRPC stub, проверить мэппинги `ShareLink`/`CloudUser`/`SharedFileEntry`.
- `MySharesViewModelTests` — пагинация, оптимистичный revoke с откатом.

---

## Принятые решения (закрытые вопросы)

1. **Вход в «Общий доступ»** — toolbar-кнопка справа сверху в `FilesRootScreen`. Иконка `link.circle` или `person.2.circle` → push к `SharedHubScreen`.
2. **Скачивание расшаренного** — `URLSession.shared.download(from:)` на переднем плане (MVP). Прогресс через `URLSessionDownloadDelegate.didWriteData`. После завершения — `UIDocumentPickerViewController(forExporting:)` для сохранения. Background download — возможное расширение позже, если будут жалобы на сворачивание.
3. **Превью входящих** — приходят прямо в `SharedWithMeEntry.file.previews[]` (`UploadFileInfo.previews`, `FilePreviewInfo { preview_url, target_width, actual_width/height }`). iOS уже умеет их парсить — переиспользуем `MediaPreview` из `CloudModels.swift:40-74`.
4. **Поведение «Создать публичную ссылку»** — системный `UIActivityViewController` (Share Sheet). Внутри пользователь сам выбирает Telegram/WhatsApp/Mail/AirDrop/«Скопировать». Текущий путь через `UIPasteboard.general.url` заменяется. Это меняет UX в:
   - `GalleryViewModel.makePublic(asset:)` (`Features/Gallery/GalleryViewModel.swift:212`)
   - `MediaGridViewModel` (`Features/Media/MediaGridViewModel.swift:251`)
   - `AlbumDetailScreen` (`Features/Media/Albums/AlbumDetailScreen.swift:144`)
   В каждом из них вместо записи в буфер запоминаем URL в `@State pendingShareURL: URL?` и вешаем `.sheet(item: $pendingShareURL) { ActivityViewController(activityItems: [$0]) }`. UIKit-обёртка `ActivityViewController: UIViewControllerRepresentable` → переиспользуемый компонент в `Features/Shared/ActivityViewController.swift`.

## Открытые вопросы

Нет.
