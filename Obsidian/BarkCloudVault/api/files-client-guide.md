# Files — Клиентский гайд по API (фото, видео, документы, каталоги, альбомы)

Parent: [[index]] · Proto: [[modules/shared-proto]] · Backend: [[modules/backend-files]] · API-обзор: [[api/files-api]]

> Назначение: самодостаточная инструкция для разработчика **клиента** (Android/iOS/web). Описывает что передать и что вернётся для каждого эндпоинта медиа-функционала: загрузка, галерея фото/видео, каталоги, альбомы. Серверная реализация — в [[modules/backend-files]].

## 0. Общее

- **Транспорт**: gRPC. Контракты: `Shared/BarkCloud.Proto/files_api.proto`, package `barkcloud.files`, C#-namespace `BarkCloud.Proto.Files`.
- **Загрузка/скачивание байтов**: обычный HTTP на файловый сервис (`POST /upload/{id}`, `GET /download/{id}`) — не gRPC.
- **Авторизация**: все gRPC-сервисы (`FilesApi`, `CloudApi`, `AlbumApi`) требуют пользовательский токен (политика `User`). Токен передаётся в gRPC-метадате заголовком `x-auth-token`. Без валидного токена — `Unauthenticated`.
- **Идентификаторы**: все ID — строки с GUID (`"6f9619ff-8b86-d011-b42d-00cf4fc964ff"`). Пустая строка `""` в `directory_id`/`parent_id` означает **корень** пользователя.
- **Время**: `google.protobuf.Timestamp` (UTC).
- **Ошибки**: доменные ошибки приходят как gRPC `FailedPrecondition` с `ErrorCode` (GUID) в trailing-метадате — см. §9. Сетевые/прочие — стандартные gRPC-статусы.

### Базовый URL для HTTP (upload/download)
Клиент **не вычисляет** его сам — серверные ответы уже содержат готовые абсолютные URL:
- `GetUploadUrl` → `url` (куда заливать).
- `UploadFileInfo.preview_url` / `previews[].preview_url` — готовые ссылки на превью (можно прямо в `<img>`).
- `GetTempDownloadUrl` → `url` (ссылка на оригинал, временная).

---

## 1. Перечисления и общие типы

### `MediaKind`
```
MEDIA_KIND_OTHER = 0;     // прочее
MEDIA_KIND_PHOTO = 1;     // фото
MEDIA_KIND_VIDEO = 2;     // видео
MEDIA_KIND_DOCUMENT = 3;  // документы (pdf/office/text)
MEDIA_KIND_AUDIO = 4;     // аудио
```
Проставляется сервером при загрузке по типу файла. Клиент использует для фильтрации галереи.

### `UploadFileType`
```
UPLOAD_FILE_TYPE_UNKNOWN = 0;
USER_AVATAR = 1;  // аватар
CLOUD_FILE = 2;   // обычный файл пользовательского облака (фото/видео/документ)
```
Для загрузки в облако всегда `CLOUD_FILE`.

### `UploadFileInfo` (карточка файла — ключевой тип ответов)
| Поле | Тип | Что значит для клиента |
|------|-----|------------------------|
| `id` | string | ID файла (блоба) |
| `file_name` | string | Имя файла |
| `file_size` | int64 | Размер в байтах |
| `media_kind` | MediaKind | Фото/Видео/Документ/Аудио |
| `image_width` / `image_height` | int32 | Размеры изображения **или кадра видео** (0 если неизвестно) |
| `previews` | repeated `FilePreviewInfo` | Превью 128/512/1024 (для фото и видео). **URL публичные** |
| `preview_url` | string | [deprecated] ссылка на самое узкое превью; используйте `previews` |
| `file_url` | string | Прямая ссылка `/download/{id}`. ⚠️ Работает только для аватаров и превью; для оригинала `CLOUD_FILE` — см. §3 (нужна временная ссылка) |
| `created_at` / `uploaded_at` | Timestamp | Время создания/завершения загрузки |
| `etag` | string | S3 ETag (служебное) |
| `uploaders` | repeated int64 | Служебное (дедуп), клиенту не нужно |

### `FilePreviewInfo`
| Поле | Тип | Описание |
|------|-----|----------|
| `preview_file_id` | string | ID превью-блоба |
| `target_width` | int32 | Запрошенная ширина: 128 / 512 / 1024 |
| `actual_width` / `actual_height` | int32 | Фактические размеры превью |
| `preview_url` | string | **Публичная** прямая ссылка — грузить напрямую в UI |

> Выбор превью под задачу: миниатюра в сетке — 128/512, полноэкранный предпросмотр — 1024. Если оригинал был узким, превью больших ширин могут отсутствовать — берите максимальное доступное.

### Cursor-пагинация (единый паттерн для всех списков)
- Первый запрос: `limit` (1..200, по умолчанию 50), курсорные поля **пустые/не заданы**.
- Ответ содержит `next_cursor_*`. Если они **пустые** — страниц больше нет.
- Следующая страница: передать значения `next_cursor_*` из предыдущего ответа в соответствующие `cursor_*` поля.
- Сортировка — от новых к старым.

---

## 2. Загрузка файла (фото / видео / документ)

Единый трёхшаговый флоу для любого типа — тип определяется сервером по имени файла.

**Шаг 1 — получить адрес загрузки** (`FilesApi.GetUploadUrl`)
- Передать: `GetUploadUrlRequest { file_type: CLOUD_FILE }`
- Вернётся: `GetUploadUrlResponse { url, file_id }`

**Шаг 2 — залить байты** (HTTP, не gRPC)
- `POST {url}` с `multipart/form-data`, поле формы **`file`** = содержимое файла (с корректным именем, например `clip.mp4` — по нему определяется content-type и `MediaKind`).
- Лимит размера: 512 МБ.
- Ответ: `200 OK` с JSON `{ "fileId": "<guid>" }`.
- ⚠️ **Важно**: возвращённый `fileId` может **отличаться** от запрошенного `file_id` — при дедупликации (файл с таким содержимым уже есть) вернётся ID существующего блоба. Всегда используйте `fileId` из ответа.

**Что происходит на сервере автоматически:**
- Фото → генерируются превью 128/512/1024.
- Видео → извлекается кадр на 5-й секунде и из него генерируются те же превью; в `image_width/height` пишутся размеры видео.
- Документы/прочее → без превью.

**Шаг 3 (опционально) — поместить в каталог** (`CloudApi.AttachFile`, см. §5) или **в альбом** (`AlbumApi.AddItemsToAlbum`, см. §6). Загруженный файл сразу попадает в галерею (§4) и без привязки к каталогу.

> ⚠️ Инвариант: один файл может быть привязан **максимум к одной директории**. Повторный `AttachFile` того же `file_id` → ошибка `FileAlreadyAttached`. В альбомах такого ограничения нет (файл может быть в нескольких альбомах).

---

## 3. Скачивание / показ

- **Превью** (миниатюры, обложки): берите `preview_url` из `FilePreviewInfo` — это публичные прямые ссылки, грузятся в UI без доп. запросов.
- **Оригинал `CLOUD_FILE`** (полное фото/видео/документ): прямой `file_url` **не сработает**. Нужно запросить временную ссылку:

`FilesApi.GetTempDownloadUrl`
- Передать: `GetTempDownloadUrlRequest { file_ids: [ ... ] }` (можно пачкой)
- Вернётся: `GetTempDownloadUrlResponse { file_urls: [ { file_id, url, preview_url } ] }`
  - `url` — временная ссылка на скачивание оригинала (TTL ограничен).
  - `preview_url` — ссылка на превью.
- Скачивать оригинал → `GET {url}`.

---

## 4. Галерея фото и видео

`CloudApi.ListUserMedia` — все фото **или** все видео пользователя (по всему облаку, независимо от каталогов), с превью.

- Передать: `ListUserMediaRequest { kind, limit, cursor_created_at, cursor_file_id }`
  - `kind`: `MEDIA_KIND_PHOTO` или `MEDIA_KIND_VIDEO`.
  - пагинация — см. §1.
- Вернётся: `ListUserMediaResponse { items[], next_cursor_created_at, next_cursor_file_id }`
  - `items[i]` = `UserImageItem { file: UploadFileInfo, entries_count, entry_names[], entry_ids[] }`
    - `file` — карточка (с превью, размерами, `media_kind`).
    - `entries_count` — в скольких **живых** записях каталога лежит файл (0 — не в каталоге; записи в корзине не считаются).
    - `entry_names` — до 5 имён записей.
    - `entry_ids` — id живых записей владельца (для переименования/удаления элемента галереи через `RenameFileEntry`/`DeleteFileEntry` без доп. листинга каталога).

> `ListUserImages` — устаревший аналог для фото; используйте `ListUserMedia(MEDIA_KIND_PHOTO)`.

Сетка фото: `kind=PHOTO`, для миниатюр брать `previews` (128/512). Сетка видео: `kind=VIDEO`, обложка = превью (кадр), для отметки «видео» можно показать иконку поверх.

---

## 5. Каталоги (файловый менеджер)

Файлы/фото/видео могут лежать в произвольных каталогах. Корень = `directory_id: ""`.

| RPC | Передать | Вернётся |
|-----|----------|----------|
| `CreateDirectory` | `{ parent_id, name }` (`parent_id` "" = корень) | `DirectoryInfo` |
| `RenameDirectory` | `{ directory_id, new_name }` | `CloudEmpty` |
| `MoveDirectory` | `{ directory_id, new_parent_id }` (""=корень) | `CloudEmpty` |
| `DeleteDirectory` | `{ directory_id }` | `CloudEmpty` (рекурсивно) |
| `ListDirectory` | `{ directory_id }` (optional; не задано/""=корень) | `DirectoryListing { subdirs[DirectoryInfo], files[FileEntryInfo] }` — только метаданные |
| `ListDirectoryDetailed` | то же | `DirectoryListingDetailed { subdirs[DirectoryInfo], files[FileEntryDetailed] }` — с полным `UploadFileInfo` (URL/превью/размеры) |
| `AttachFile` | `{ directory_id, file_id, name }` | `CloudEmpty` |
| `RenameFileEntry` | `{ entry_id, new_name }` | `CloudEmpty` |
| `MoveFileEntry` | `{ entry_id, new_directory_id }` (""=корень) | `CloudEmpty` |
| `DeleteFileEntry` | `{ entry_id }` | `CloudEmpty` (сам файл не удаляется из облака сразу) |
| `GetPath` | `{ directory_id }` **или** `{ entry_id }` (oneof) | `PathResponse { segments[PathSegment{id,name}], full_path }` |

Типы:
- `DirectoryInfo { id, parent_id, name, created_at, updated_at }`
- `FileEntryInfo { id, directory_id, file_id, name, created_at }` — `id` это ID **записи** (entry), `file_id` — ID блоба.
- `FileEntryDetailed { entry: FileEntryInfo, file: UploadFileInfo }`

> Для миниатюр в листинге каталога используйте `ListDirectoryDetailed` (там сразу есть превью). `ListDirectory` — для дешёвой навигации без картинок.

---

## 6. Альбомы (фото + видео)

Альбом — универсальная коллекция (фото и видео вместе). Один файл может быть в нескольких альбомах. Сервис `AlbumApi`.

| RPC | Передать | Вернётся |
|-----|----------|----------|
| `CreateAlbum` | `{ name, description }` | `AlbumInfo` |
| `UpdateAlbum` | `{ album_id, name?, description?, cover_file_id? }` (optional поля; пропущенные не меняются; `cover_file_id=""` — сброс обложки) | `AlbumInfo` |
| `DeleteAlbum` | `{ album_id }` | `CloudEmpty` (файлы остаются в облаке) |
| `AddItemsToAlbum` | `{ album_id, file_ids[] }` | `CloudEmpty` |
| `RemoveItemsFromAlbum` | `{ album_id, file_ids[] }` | `CloudEmpty` |
| `ListAlbums` | `{ limit, cursor_updated_at, cursor_album_id }` | `ListAlbumsResponse { albums[AlbumInfo], next_cursor_updated_at, next_cursor_album_id }` |
| `ListAlbumItems` | `{ album_id, limit, cursor_added_at, cursor_file_id, kind_filter? }` | `ListAlbumItemsResponse { items[AlbumItemEntry], next_cursor_added_at, next_cursor_file_id }` |

Типы:
- `AlbumInfo { id, name, description, cover_file_id, cover_preview_url, items_count, created_at, updated_at }`
  - `cover_preview_url` — готовая ссылка на превью обложки (~512px) для карточки альбома; пустая, если обложки/превью нет.
  - `items_count` — количество элементов (для подписи «N фото»).
- `AlbumItemEntry { file: UploadFileInfo, added_at }`

Поведение, важное для UI:
- `AddItemsToAlbum`: принимаются только **фото/видео**, принадлежащие пользователю; дубли игнорируются. Если у альбома ещё нет обложки — первой добавленной становится обложка.
- `RemoveItemsFromAlbum`: при удалении файла-обложки она автоматически переустановится на первый оставшийся элемент (или сбросится).
- `ListAlbumItems.kind_filter` (опц.): показать в альбоме только фото (`MEDIA_KIND_PHOTO`) или только видео (`MEDIA_KIND_VIDEO`); без него — все.
- `ListAlbums` отдаёт **все** альбомы пользователя — на странице «Фото» и «Видео» показываются одни и те же альбомы.

**Типовой флоу**: создать (`CreateAlbum`) → наполнить (`AddItemsToAlbum` с `file_id` уже загруженных файлов) → показать список (`ListAlbums`) → открыть (`ListAlbumItems`, превью из `file.previews`) → при тапе на элемент оригинал тянуть через `GetTempDownloadUrl` (§3).

---

## 7. Смена обложки видео вручную

`CloudApi.SetVideoThumbnail`
- Предусловие: картинка-кадр уже загружена как обычный файл (§2) и есть её `file_id`.
- Передать: `SetVideoThumbnailRequest { video_file_id, source_image_file_id }`
- Вернётся: `CloudEmpty`
- Эффект: старые превью видео заменяются новыми (из картинки). Оба файла должны принадлежать пользователю; `video_file_id` — видео, `source_image_file_id` — фото. Иначе ошибки `CloudAccessDenied` / `InvalidThumbnailSource`.

---

## 8. Квота хранилища

`FilesApi.GetUserStorageInfo`
- Передать: `GetUserStorageInfoRequest {}`
- Вернётся: `GetUserStorageInfoResponse { total_used_storage, storage_limit, storage_by_types[] }` (байты).

---

## 8a. Проверка наличия по SHA256-хешу

Два эндпоинта, **не путать**:

`FilesApi.CheckFileHash` (одиночный)
- Передать: `CheckFileHashRequest { file_hash }` (SHA256 hex, 64 символа).
- Вернётся: `CheckFileHashResponse { file_id }` (пусто, если нет).
- ⚠️ **Побочный эффект**: добавляет текущего пользователя в uploaders найденного блоба. Предназначен для дедупликации **в процессе загрузки**, а не для пассивной проверки.

`FilesApi.CheckFileHashes` (пакетный, **без побочных эффектов**)
- Передать: `CheckFileHashesRequest { file_hashes: [ ... ] }` (до 500 валидных уникальных; сервер нормализует к lowercase, отбрасывает некорректные/дубли).
- Вернётся: `CheckFileHashesResponse { results: [ { file_hash, exists } ] }` — по одному результату на валидный уникальный хеш.
- Когда: пассивная индикация «уже в облаке» (напр. иконка облака на фото из медиатеки устройства). Хеш — SHA256 байтов оригинала (тех же, что отправляются на `/web/upload`).

---

## 9. Ошибки (доменные)

Приходят как gRPC `FailedPrecondition`, в trailing-метадате — `ErrorCode` (GUID). Сопоставление по смыслу:
- `FileNotFound` — нет файла/блоба.
- `CloudAccessDenied` — объект не принадлежит пользователю.
- `FileAlreadyAttached` — файл уже привязан к директории (нарушение «одна директория на файл»).
- `DirectoryNotFound`, `DirectoryNameConflict`, `CircularMove` — операции с каталогами.
- `AlbumNotFound`, `AlbumNameConflict` — операции с альбомами.
- `InvalidThumbnailSource` — неверные аргументы `SetVideoThumbnail`.

Клиенту достаточно показать локализованное сообщение и/или обработать ключевые коды (например, `FileAlreadyAttached`, `*NameConflict`).

---

## 10. Шпаргалка «экран → вызовы»

- **Лента/сетка фото**: `ListUserMedia(PHOTO)` → миниатюры из `previews`.
- **Лента/сетка видео**: `ListUserMedia(VIDEO)` → обложка из `previews` (кадр).
- **Открыть фото/видео в полном размере**: `GetTempDownloadUrl([file_id])` → `url`.
- **Список альбомов**: `ListAlbums` → карточки (`cover_preview_url`, `items_count`).
- **Открыть альбом**: `ListAlbumItems(album_id)`.
- **Создать/наполнить альбом**: `CreateAlbum` → `AddItemsToAlbum`.
- **Файловый менеджер**: `ListDirectoryDetailed(directory_id)`; навигация — `GetPath`.
- **Загрузить новый файл**: `GetUploadUrl(CLOUD_FILE)` → `POST {url}` (form-field `file`) → (опц.) `AttachFile` / `AddItemsToAlbum`.
