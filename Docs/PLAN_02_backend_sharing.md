# Plan 02 — Backend Files (шаринг): публичная страница + приватные гранты

> Сервис: `Backend/BarkCloud.Files`. Контракты: `Shared/BarkCloud.Proto/files_api.proto`. Веб-прокси: `Backend/BarkCloud.Web`.
> Сборка перед каждым коммитом: `dotnet build BarkCloud.slnx` (или затронутые проекты). Тесты: `dotnet test` по `Tests/Backend/BarkCloud.Files.Tests`.
> Каждая задача = коммит (без push). После всего плана — финальный коммит.
> Все proto-правки **аддитивные** (Android/iOS/Drive не ломаются). Android-копию proto НЕ трогаем.

## Текущее состояние (после Plan 01, шаринг им не затронут)

- **Публичные ссылки** (только они и есть): `ShareLink {Id, OwnerId, FileId, Token, Name, CreatedAt, ClickCount}`; `CreateShare`/`ListMyShares`/`RevokeShare` (CloudApi, user-токен); `ResolveShare` (FilesServerApi, сервисный токен, анонимно) → `TempFile` → `download_url`. Веб `/s/{token}` → 302.
- **Превью уже публичны де-факто** (`FilesController` анонимен; `/download/{previewFileId}`). Оригинал `CloudFile` — только через `TempFile` (`DownloadFileCommandHandler`).
- **Шаринга между пользователями и «мне доступны» НЕТ.** `SearchUsers` в Users есть; в вебе пока подключён только для... (нет, не подключён — подключим).
- TempFile создаётся `ITempFilesStorage.CreateTempFilesBatchAsync(new[]{fileId})`; URL — `FileUrlHelper.GetPublicBaseUrl` + `GenerateDownloadUrl`.

## Принятые решения (из обсуждения)

- **Приватные гранты** (сущность `FileGrant` владелец→получатель→файл), а не публичная ссылка для user-to-user.
- **Права получателя:** только просмотр и скачивание (без копии в своё хранилище, без ре-шаринга).
- **Доступ получателя к файлу — НЕ по токену.** Получатель видит файл в «Общие → мне доступны» (авторизованный), открывает его в том же вьюере (Plan 04, SPA `/v/...`), скачивает через grant-проверенный `TempFile`. Никакого секретного URL владелец не пересылает — доступ привязан к аккаунту получателя.
- **Имя «от кого»** резолвит веб-слой через Users (по `owner_user_id`), чтобы не вводить связь Files→Users. Backend отдаёт только `owner_user_id`.
- Публичная ссылка остаётся бессрочной (как сейчас); EXIF на публичной странице не отдаём (приватность) — это рендер Plan 04.

## Контракт (аддитивные proto-правки)

```proto
// FilesServerApi.ResolveShareResponse — данные для ПУБЛИЧНОЙ страницы фото/видео:
message ResolveShareResponse {
  bool found = 1; string file_id = 2; string name = 3; string download_url = 4;
  MediaKind media_kind = 5; string preview_url = 6; // публичный preview (фото/видео), пусто если нет
  int32 image_width = 7; int32 image_height = 8; int64 file_size = 9;
}

// CloudApi (user-токен): пер-юзер шаринг
rpc ShareFileWithUser   (ShareFileWithUserRequest)   returns (CloudEmpty);
rpc RevokeUserShare     (RevokeUserShareRequest)     returns (CloudEmpty);
rpc ListSharedWithMe    (ListSharedWithMeRequest)    returns (ListSharedWithMeResponse);
rpc ListMyOutgoingShares(ListMyOutgoingSharesRequest)returns (ListMyOutgoingSharesResponse);
rpc GetSharedFileDownloadUrl(GetSharedFileDownloadUrlRequest) returns (GetSharedFileDownloadUrlResponse);

message ShareFileWithUserRequest { string file_id = 1; int64 recipient_user_id = 2; }
message RevokeUserShareRequest   { string grant_id = 1; }
message ListSharedWithMeRequest  { int32 limit = 1; google.protobuf.Timestamp cursor_shared_at = 2; string cursor_grant_id = 3; }
message SharedWithMeEntry        { string grant_id = 1; UploadFileInfo file = 2; int64 owner_user_id = 3; google.protobuf.Timestamp shared_at = 4; }
message ListSharedWithMeResponse { repeated SharedWithMeEntry items = 1; google.protobuf.Timestamp next_cursor_shared_at = 2; string next_cursor_grant_id = 3; }
message ListMyOutgoingSharesRequest  { string file_id = 1; }
message OutgoingShareEntry           { string grant_id = 1; int64 recipient_user_id = 2; google.protobuf.Timestamp shared_at = 3; }
message ListMyOutgoingSharesResponse { repeated OutgoingShareEntry items = 1; }
message GetSharedFileDownloadUrlRequest  { string file_id = 1; }
message GetSharedFileDownloadUrlResponse { string download_url = 1; }
```

---

## Задача 2.1 — Публичная страница: данные превью в `ResolveShare`

**Цель:** при резолве публичного токена возвращать тип медиа, публичный preview-URL и размеры/размер — чтобы Plan 04 отрисовал страницу с превью, а не только редирект.

**Файлы:** `Shared/BarkCloud.Proto/files_api.proto` (ResolveShareResponse), `Features/Cloud/ResolveShare/ResolveShareCommandHandler.cs`, `Persistence/IUploadedFilesStorage.cs` (`GetPreviewsForFile`), `Helpers/FileUrlHelper.cs`.

**Шаги:** расширить `ResolveShareResponse`; в хендлере подтянуть `file.MediaKind`, `ImageWidth/Height`, `Size`, превью (`GetPreviewsForFile` → выбрать средний/крупный → `GenerateDownloadUrl(previewFileId)`), заполнить поля. `download_url` оригинала остаётся.

**Проверка:** сборка; юнит-тест: резолв фото-ссылки → `media_kind=PHOTO`, непустой `preview_url`, размеры.

---

## Задача 2.2 — Домен `FileGrant` + хранилище + миграция

**Файлы:** `Domain/FileGrant.cs` (new), `Persistence/IGrantStorage.cs`+`GrantStorage.cs` (new), `Persistence/FilesContext.cs` (DbSet + индексы), `Persistence/Migrations/` (миграция), `Program.cs` (DI регистрация хранилища).

**Шаги:**
1. `FileGrant { Id, OwnerId, RecipientId, FileId, CreatedAt }`.
2. Индексы: уникальный `(OwnerId, FileId, RecipientId)` (идемпотентность), `(RecipientId, CreatedAt)` (cursor «мне доступны»), `FileId` (чистка).
3. `GrantStorage`: `Add`, `Exists(owner, file, recipient)`, `GrantExistsForRecipient(recipient, file)`, `GetById`, `Remove`, `ListSharedWithMePage(recipient, cursor…)`, `ListByOwnerFile(owner, file)`, `RemoveByFile(owner, file)`, `RemoveForUser(userId)` (как owner и как recipient).
4. Миграция через `dotnet ef migrations add AddFileGrants`.

**Проверка:** сборка; миграция применяется.

---

## Задача 2.3 — Шаринг владельцем: `ShareFileWithUser` / `RevokeUserShare` / `ListMyOutgoingShares`

**Файлы:** `Features/Cloud/ShareFileWithUser/…`, `RevokeUserShare/…`, `ListMyOutgoingShares/…`, `Host/CloudApiService.cs`, proto.

**Шаги:**
1. `ShareFileWithUser`: владелец владеет файлом (`file.Uploaders.Contains(ownerId)`, иначе `CloudAccessDeniedException`); `recipient != owner`; идемпотентно (если грант есть — no-op). Добавить `FileGrant`.
2. `RevokeUserShare(grant_id)`: только владелец гранта (`grant.OwnerId == userId`); удалить.
3. `ListMyOutgoingShares(file_id)`: гранты владельца на файл (для управления «с кем поделено»).

**Проверка:** сборка; тесты: чужой файл → AccessDenied; шаринг себе → отклон/no-op; повторный шаринг идемпотентен; revoke чужого гранта → AccessDenied.

---

## Задача 2.4 — Доступ получателя: `ListSharedWithMe` / `GetSharedFileDownloadUrl` (безопасность)

**Файлы:** `Features/Cloud/ListSharedWithMe/…`, `GetSharedFileDownloadUrl/…`, `Mapping/UploadFileMapping.cs` (переиспользовать для `UploadFileInfo`), `Host/CloudApiService.cs`, proto.

**Шаги:**
1. `ListSharedWithMe(cursor)`: гранты, где `RecipientId == UserContext.UserId` (строго!); по каждому собрать `UploadFileInfo` (через существующий маппинг; превью публичны), `owner_user_id`, `shared_at`. Пропускать гранты, чей файл удалён.
2. `GetSharedFileDownloadUrl(file_id)`: проверить `GrantExistsForRecipient(userId, file_id)` (иначе AccessDenied); создать `TempFile` (как `ResolveShare`); вернуть URL. **Не обходить** `DownloadFileCommandHandler` — только через TempFile.

**Безопасность:** оба пути строго фильтруют по `UserContext.UserId`. Ошибка фильтра = утечка чужих файлов.

**Проверка:** сборка; тесты: `ListSharedWithMe` отдаёт только мои гранты; `GetSharedFileDownloadUrl` без гранта → AccessDenied; с грантом → TempFile-URL.

---

## Задача 2.5 — Чистка грантов: `UserDeleted` + `TrashPurge`

**Файлы:** `Consumers/UserDeletedConsumer.cs`, `Services/TrashPurgeService.cs`.

**Шаги:**
1. `UserDeletedConsumer`: удалить `FileGrant`, где `OwnerId == deleted` ИЛИ `RecipientId == deleted` (не оставлять висящие гранты на/от удалённого).
2. `TrashPurgeService`: при окончательном удалении блоба (per-pair цикл, рядом с `AlbumItems/Favorites/ShareLinks`) удалить `FileGrant` владельца на этот `FileId` — иначе у получателя останется битая запись «мне доступны».

**Проверка:** сборка; тесты консьюмера/сервиса (по образцу существующих).

---

## Задача 2.6 — Веб-прокси + финал

**Файлы:** `Backend/BarkCloud.Web/Endpoints/CloudApiEndpoints.cs`, `Program.cs` (если нужен Users-клиент).

**Шаги:**
1. `GET /api/shared/users/search?q=` → прокси `UsersApi.SearchUsers` (выбор получателя; min 2 симв.).
2. `POST /api/shared/grant {fileId, recipientUserId}` → `ShareFileWithUser`.
3. `POST /api/shared/revoke-grant {grantId}` → `RevokeUserShare`.
4. `GET /api/shared/with-me` → `ListSharedWithMe`; резолв имён «от кого» по `owner_user_id` через Users (батч; метод уточнить — `ListByIds`/`GetById`).
5. `GET /api/shared/outgoing?fileId=` → `ListMyOutgoingShares` (для управления).
6. `POST /api/shared/download {fileId}` → `GetSharedFileDownloadUrl`.
7. Полная сборка + тесты Files. **Финальный коммит плана.**

**UI не делаем** — это Plan 04 (публичная страница `/v/:token`, модалка «поделиться», вкладка «Общие → мне доступны»).

## Обновление памяти проекта

Обновить `Obsidian/BarkCloudVault/modules/backend-files.md`, `api/files-api.md` (FileGrant, новые RPC, ResolveShare-превью, чистка грантов). Changelog не ведём.
