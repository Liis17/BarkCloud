# Plan 06 — iOS: дубликаты, модалка, автозагрузка (без сборки)

> Клиент: `Ios/BarkCloud`. **Сборка не делается** (хост Windows) — изменения верифицируются анализом кода. Swift-правки точечные и обратимые.

## Анализ (что выяснено)

- **Ручная загрузка уже грузит безусловно**: `GalleryViewModel.uploadSelected` и `MediaGridViewModel.uploadAssets` вызывают `cloud.uploadFile(...)` напрямую — после снятия серверного дедупа (Plan 01) это создаёт дубликаты. Менять не нужно.
- **`ensureCloudFileID`** (резолв `file_id` по хешу для одиночных действий над существующим облачным файлом — напр. добавить в альбом) — намеренно резолвит существующий, не трогаем.
- **Единственная блокировка дубликатов — UX пикера** `DeviceAssetPickerScreen` (`blockAlreadyUploaded=true` гасит и не даёт выбрать уже загруженные). Это и правим.
- **Автозагрузка-skip уже работает**: `BackupManager.classify` раскладывает по серверному хешу (`CheckFileHashes`): есть в облаке → `reclaimable` (пропуск), нет → `pendingUpload`. После снятия дедупа `FileHash`-строки пишутся per-blob, поэтому `exists=true` сохраняется — автозагрузка продолжает пропускать загруженное. **Изменений не требуется** (верификация).

## Задача 6.1 — Разрешить дубликаты + модалка в пикере

**Файл:** `Ios/BarkCloud/BarkCloud/Features/Gallery/DeviceAssetPickerScreen.swift`.

**Шаги:**
1. `selectedAssets()` — вернуть все выбранные (убрать фильтр-отсечение уже-в-облаке).
2. Сетка — убрать блокировку (`opacity 0.45` + запрет тапа) для уже-загруженных; оставить бейдж «уже в облаке».
3. `blockAlreadyUploaded` переосмыслить как «предупреждать о дубликатах» (галерея=true, альбом=false — вызывающие не меняются).
4. При подтверждении: если `blockAlreadyUploaded` и среди выбранных есть уже-в-облаке — показать `confirmationDialog` «часть файлов уже в облаке, загрузить ещё раз?» с выбором «Загрузить всё / Только новые / Отмена»; иначе — сразу `onConfirm`.

**Проверка:** анализ кода (сборка недоступна). Логика: дубликаты теперь выбираемы; модалка спрашивает перед повторной загрузкой; «Только новые» грузит лишь не-дубликаты.

## Задача 6.2 — Маршрутизация папок iOS (передние загрузки) — ВЫПОЛНЕНО

Поле `route_by_media_kind` **уже присутствует** в checked-in `Generated/Proto/files_api.pb.swift` (`Barkcloud_Files_AttachFileRequest.routeByMediaKind`, регенерировано при позднем мердже) — отдельная регенерация proto не нужна.

**Сделано:**
- `CloudRepository.attachFile` += `routeByMediaKind: Bool = false` → пробрасывает в `AttachFileRequest.route_by_media_kind`.
- `CloudRepository.uploadFile` += `routeByMediaKind: Bool = false` → при `true` привязывает без папки с флагом (сервер раскладывает по «Фото»/«Видео»/«Другие документы»).
- Передние загрузки переведены на `routeByMediaKind: true`: `GalleryViewModel.uploadSelected`, `GalleryViewModel.ensureCloudFileID`, `MediaGridViewModel.uploadAssets` (убран `ensureRecentUploadsFolder()` в этих местах).

**Сборка не делалась** (хост Windows/Linux, нет macOS) — изменения верифицированы анализом кода.

### Остаётся (фоновые загрузки)

`ensureRecentUploadsFolder`/`recentUploadsFolderName` **НЕ удалены**: их ещё используют фоновые пути — `BackupManager` (автозагрузка) и `ShareInboxUploader`/Share Extension. Там привязка отложена через персистентный `UploadJob` (SwiftData `@Model`, общий с отдельным таргетом Share Extension), и проброс `route_by_media_kind` потребовал бы миграции схемы `UploadJob` + правки второго таргета + отложенного attach в `AppEnvironment.onJobCompleted` — это вне безопасного/верифицируемого скоупа без сборки на macOS. После их перевода хелпер можно убрать.

## Финал

Обновить vault `modules/ios-app.md`. Коммит плана.
