# Plan 03 — Веб-клиент (React): загрузка, дефолтные папки, ПКМ-меню

> Клиент: `Backend/BarkCloud.Web/ClientApp/` (React + TS + Vite). Верификация: `npm run typecheck` (tsc --noEmit) перед каждым коммитом; при существенных правках — `npm run build`.
> Каждая задача = коммит (без push). После плана — финальный коммит. Учитывает фактический контракт после Plan 01/02.

## Контракт от бэкенда (готов)

- `POST /api/files/check-hash` → `{ fileId, exists, locations: [{ entryId, name, directoryId, directoryName }] }` (без побочных эффектов).
- `POST /api/cloud/attach` принимает `{ dir?, fileId, name, routeByMediaKind? }`. `routeByMediaKind=true` → сервер кладёт в системную папку по типу (Фото/Видео/Другие документы), игнорируя `dir`.
- `POST /api/cloud/favorites/add` `{ fileId }` — добавить в избранное (готово).

## Задача 3.1 — Переработка загрузки: модалка дубликата + дефолтные папки

**Цель:** убрать «тихий» пропуск дубликата; при совпадении хеша показывать модалку «такой файл уже есть» (имя + папка) с выбором «загрузить ещё раз / нет». Загрузки с вкладок Фото/Видео распределять по типу (`routeByMediaKind=true`); «Недавно загруженные» больше не создавать. Загрузка в открытую папку (FilesPage `currentDir`) — без авто-распределения.

**Файлы:** `lib/api.ts` (uploadFile/checkDuplicate), новый `hooks/useDuplicatePrompt.tsx`, `pages/PhotosPage.tsx` + `pages/VideosPage.tsx` (doUpload, убрать `RECENT_FOLDER`/`ensureRecentFolder`), `pages/FilesPage.tsx` (doUpload — currentDir без изменений семантики), `components/ui/Modal.tsx`/`ConfirmModal.tsx` (образец).

**Шаги:**
1. `lib/api.ts`: добавить тип `DuplicateLocation`; функцию `checkDuplicate(file) → { exists, locations }` (sha256 + POST check-hash; нет хеша → `{exists:false}`). Из `uploadFile` убрать предзагрузочный дедуп-блок — теперь всегда `uploadXhr`. Поле `deduped` из `UploadResult` убрать (обновить вызывающих).
2. `hooks/useDuplicatePrompt.tsx`: хук `{ ask(file, locations) → Promise<boolean>, overlay }` на базе `Modal` — показывает имя файла, где лежит (папка/корень), кнопки «Загрузить ещё раз» / «Пропустить».
3. doUpload (Photos/Videos/Files): на каждый файл — `checkDuplicate`; если `exists` — `await ask(...)`, при отказе пропустить; иначе `uploadFile` + attach.
4. Photos/Videos: attach с `routeByMediaKind: true`; удалить `RECENT_FOLDER`/`ensureRecentFolder`. FilesPage: attach с `dir: currentDir` (как сейчас, без route).

**Проверка:** `npm run typecheck` зелёный. Ручная логика: дубль → модалка; «ещё раз» → грузит копию; «пропустить» → пропуск.

**Открытые вопросы/заметки:** при множественной загрузке модалка спрашивает по каждому дублю (без «применить ко всем» в v1). Если `crypto.subtle` недоступен (http) — дубль не детектится, грузим как есть.

## Задача 3.2 — ПКМ «Добавить в избранное»

**Файлы:** `hooks/useMediaActions.tsx` (buildItems), `components/Icon.tsx` (иконка `star` есть), опц. `pages/FilesPage.tsx` (fileMenu).

**Шаги:** добавить действие `addToFavorites(m)` (`apiPost('/api/cloud/favorites/add', { fileId: m.id })` + toast) и пункт меню `{ label: 'Добавить в избранное', icon: 'star', onClick }` рядом с альбомными пунктами. Пункт **add-only** (toggle потребовал бы поля `isFavorite` в контракте — вне скоупа). Опц. тот же пункт в `FilesPage.fileMenu` для документов.

**Проверка:** `npm run typecheck`; пункт появляется в ПКМ фото/видео, клик добавляет в избранное.

## Задача 3.3 — Починка ПКМ в альбомах

**Файлы:** `components/albums/AlbumDetail.tsx`, `pages/PhotosPage.tsx`/`pages/VideosPage.tsx` (проброс пропсов, если нужно).

**Шаги:** в `AlbumDetail` подключить `useMediaActions` (или узкое меню), навесить `onContextMenu={(e)=>openMenu(e, m)}` на карточку элемента и отрендерить overlay. Учесть: элементы альбома — `CardFile` без `entryIds`, поэтому пункты «Переименовать/Удалить/Показать в папке» будут disabled (guard `hasEntry` уже есть). Пункт «Добавить в избранное» (из 3.2) и «Убрать из альбома» работают.

**Проверка:** `npm run typecheck`; ПКМ по фото/видео внутри альбома открывает меню.

## Финал

`npm run build` (полная сборка ClientApp) + проверка, что `dotnet build BarkCloud.Web` проходит. Обновить vault-заметку `modules/backend-web.md` (модалка дубликата, авто-папки, ПКМ-избранное/альбомы). Финальный коммит плана.
