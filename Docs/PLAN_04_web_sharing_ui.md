# Plan 04 — Веб-клиент (React): шаринг-UI

> Клиент: `Backend/BarkCloud.Web/ClientApp/` (React). Серверная часть веба: `Backend/BarkCloud.Web` (C#). Верификация: `npm run typecheck`/`npm run build` (React) и `dotnet build BarkCloud.Web` (C#) перед коммитами.
> Опирается на готовый backend Plan 02 (`/api/shared/*`, `ResolveShare` с превью).

## Решения

- **Публичная страница** — SPA-маршрут `/v/:token` вне `AppShell`; `MapFallback` отдаёт `index.html` для `/v/*` без авторизации. Страница тянет метаданные анонимно и показывает превью (фото/видео) + кнопку «Скачать».
- **Две ссылки:** страница `/v/{token}` (основная, шарят её) + прямое скачивание `/s/{token}` (302, уже есть). Превью публичны. `ShareJson.url` меняем на `/v/{token}` (страница), `downloadUrl` = `/s/{token}`.
- **Поделиться с пользователем** — пункт ПКМ «Поделиться с пользователем» открывает модалку с поиском (`/api/shared/users/search`) и грантом (`/api/shared/grant`).
- **«Мне доступны»** — второй таб на `SharedPage` (`/api/shared/with-me`): файл, от кого, когда; «Скачать» через `/api/shared/download`.
- Публичная страница НЕ использует авторизованный `api()` (он редиректит на `/login` при 401) — только `fetch`.

## Задача 4.1 — Backend-web: анонимный эндпоинт + публичный маршрут

**Файлы:** `Backend/BarkCloud.Web/WebEndpoints.cs` (анонимный `/s/{token}/info`), `Backend/BarkCloud.Web/Program.cs` (MapFallback исключение для `/v/`), `Backend/BarkCloud.Web/Endpoints/CloudApiEndpoints.cs` (`ShareJson.url` → `/v/{token}`).

**Шаги:**
1. `WebEndpoints.cs`: анонимный `GET /s/{token}/info` → `FilesServerApi.ResolveShare(token)` → JSON `{ found, name, mediaKind, previewUrl, imageWidth, imageHeight, fileSize, downloadPath: "/s/"+token }`. (`media_kind` → строка.)
2. `Program.cs` `MapFallback`: если путь начинается с `/v` — отдать `index.html` БЕЗ `AuthenticateAsync` (публичная страница).
3. `ShareJson` (`CloudApiEndpoints.cs:566`): `url` = `…/v/{token}` (страница) вместо `/s/{token}`; добавить `downloadUrl` = `/s/{token}`.

**Проверка:** `dotnet build BarkCloud.Web`. `GET /s/{token}/info` отдаёт JSON; `/v/x` отдаёт index.html без редиректа.

## Задача 4.2 — React: публичная страница `/v/:token`

**Файлы:** новый `pages/PublicViewPage.tsx`, `main.tsx` (маршрут вне AppShell), стили (по необходимости).

**Шаги:**
1. `PublicViewPage`: `useParams` token; `fetch('/s/'+token+'/info')` (без cookie-зависимости); состояние loading/notfound/ok.
2. Рендер: для фото/видео — превью (`previewUrl`); имя, размер; кнопка «Скачать» (`<a href={'/s/'+token} download>`). Минимальный самодостаточный layout (не `AppShell`).
3. `main.tsx`: `<Route path="v/:token" element={<PublicViewPage/>} />` ПЕРЕД `<Route element={<AppShell/>}>`.

**Проверка:** `npm run typecheck`; страница открывается без авторизации, показывает превью+скачать.

## Задача 4.3 — Модалка «Поделиться с пользователем» + пункт ПКМ

**Файлы:** новый `components/ui/ShareWithUserModal.tsx`, `hooks/useMediaActions.tsx` (пункт + состояние), `components/albums/AlbumDetail.tsx` (пункт), `lib/types.ts` (тип User при необходимости).

**Шаги:**
1. `ShareWithUserModal`: поле поиска (debounce) → `GET /api/shared/users/search?q=` → список (аватар/имя/username); выбор → `POST /api/shared/grant { fileId, recipientUserId }` → toast «Доступ выдан».
2. В `useMediaActions.buildItems` пункт «Поделиться с пользователем» (icon `share`/`user`) рядом с «Создать публичную ссылку»; открывает модалку (состояние `shareWith`).
3. В `AlbumDetail.itemMenu` — тот же пункт.

**Проверка:** `npm run typecheck`; поиск находит пользователей, грант выдаётся.

## Задача 4.4 — «Мне доступны» на SharedPage

**Файлы:** `pages/SharedPage.tsx`.

**Шаги:**
1. Добавить переключатель табов: «Мои публичные» / «Мне доступны».
2. Таб «Мне доступны»: `GET /api/shared/with-me` → карточки: превью/имя файла, «от кого» (имя/username/аватар), когда; кнопка «Скачать» (`POST /api/shared/download {fileId}` → открыть `downloadUrl`); cursor-пагинация при необходимости.

**Проверка:** `npm run typecheck` + `npm run build`. `dotnet build BarkCloud.Web`. Финальный коммит плана; обновить vault `modules/backend-web.md`.
