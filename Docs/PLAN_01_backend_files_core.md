# Plan 01 — Backend Files (ядро): дубликаты, дефолтные папки, фикс альбома

> Сервис: `Backend/BarkCloud.Files`. Контракты: `Shared/BarkCloud.Proto/files_api.proto`. Веб-прокси: `Backend/BarkCloud.Web/Endpoints/CloudApiEndpoints.cs`.
> Сборка перед каждым коммитом: `dotnet build BarkCloud.slnx`. Тесты: `dotnet test` по `Tests/Backend/BarkCloud.Files.Tests`.
> Каждая задача = отдельный коммит (без push). После всего плана — финальный коммит.
> Все proto-изменения здесь **аддитивные** (не ломают Drive/iOS/Android). Android-копию proto НЕ трогаем.

---

## Задача 1.1 — Снять серверный дедуп по хешу (независимые копии)

**Цель:** каждая загрузка сохраняется как отдельный блоб; одинаковый контент больше не схлопывается.

**Файлы:**
- `Features/UploadFile/UploadFileCommandHandler.cs:216-255` — блок «Серверная дедупликация».
- `Persistence/FileHashesStorage.cs` — `GetFileIdByHash`/`AddHash`/`DeleteHashByFileId`.
- `Domain/FileHash.cs`, `Persistence/FilesContext.cs` (индекс `IX_FileHashes_Hash` уже неуникальный — менять не нужно).

**Шаги:**
1. Убрать короткое замыкание дедупа (`GetFileIdByHash` → `DeleteFile`/возврат `existingFileId`). Всегда грузить блоб в S3 под `file.Id` и писать собственную строку `FileHash` (`AddHash`).
2. Превью: оставить обычный путь генерации. `PreviewPersistenceService` дедуплицирует превью-блобы по SHA256 и ведёт ref-count — проверить, что удаление одной копии (`DeleteFile`/`OrphanBlobCleanupService`) не сносит общий превью-блоб, пока есть другие владельцы.
3. Удалить ставший неиспользуемым код/ветки, возникшие из-за снятия дедупа (по правилу «убирай своих сирот»).

**Проверка:**
- `dotnet build BarkCloud.slnx` зелёный.
- Переписать `Tests/Backend/BarkCloud.Files.Tests/Features/UploadFile/UploadFileCommandHandlerTests.cs`: тест(ы) `Handle_Deduplicates…` → новое поведение «две одинаковые загрузки = два `file_id`, две строки `FileHash`, оба блоба в S3». `dotnet test` зелёный.

**Риски:** рост места в S3/квоте (принято); `TrashPurgeService`/`OrphanBlobCleanupService` чистят по `FileId` (безопасно при неуникальном хеше) — подтвердить, что подчистка не удаляет чужой блоб/общее превью.

---

## Задача 1.2 — Авто-переименование при коллизии имени в одной директории

**Цель:** при привязке файла в директорию, где уже есть живая запись с таким именем, новый файл получает имя с постфиксом ` (1)`, ` (2)`… вместо ошибки.

**Файлы:**
- `Features/Cloud/AttachFile/AttachFileCommandHandler.cs:71-72` — сейчас `FileEntryNameExists` → `throw DirectoryNameConflictException()`.
- `Features/Cloud/RestoreFromTrash/RestoreFromTrashCommandHandler.cs:73-91` — готовый `ResolveName` (постфикс ` (i)`).
- `Persistence/CloudHierarchyStorage.cs:140-145` — `FileEntryNameExists` (источник истины коллизии).

**Шаги:**
1. Вынести `ResolveName` в переиспользуемый хелпер (общий для `RestoreFromTrash` и `AttachFile`) либо метод хранилища `ICloudHierarchyStorage.ResolveUniqueName(ownerId, directoryId, name)`. Логику постфикса не менять.
2. В `AttachFile`: вместо исключения при коллизии — получить уникальное имя через хелпер и привязать под ним. Имя ответа клиенту НЕ обязателен (см. 1.6: `AttachFile` остаётся `CloudEmpty`; клиент видит финальное имя при перезагрузке листинга).

**Проверка:** `dotnet build` зелёный; юнит-тест: привязка двух файлов с одинаковым именем в одну папку → второй получает ` (1)`.

**Риски:** уникальный частичный индекс `(OwnerId, DirectoryId, Name) WHERE IsDeleted=false` остаётся гарантом — `ResolveUniqueName` обязан опираться на живые записи.

---

## Задача 1.3 — Системные папки Фото/Видео/Другие документы + маршрутизация по типу

**Цель:** при загрузке без явной папки сервер кладёт фото→«Фото», видео→«Видео», прочее→«Другие документы»; при явной папке — кладёт туда. «Недавно загруженные» сервером не используется.

**Файлы:**
- `Domain/CloudDirectory.cs` — добавить `SystemKind` (enum).
- `Domain/MediaKind.cs` — существующий enum (Other/Photo/Video/Document/Audio).
- `Persistence/FilesContext.cs` — EF-конфигурация `SystemKind` + индекс для быстрого ensure (напр. частичный по `(OwnerId, SystemKind) WHERE SystemKind <> None`).
- `Persistence/Migrations/` — новая EF-миграция (добавление колонки `SystemKind`).
- `Persistence/CloudHierarchyStorage.cs` — добавить `EnsureSystemDirectory(ownerId, systemKind)` (find-or-create по флагу, в корне).
- `Features/Cloud/AttachFile/AttachFileCommandHandler.cs:51-54` (ветка root) и `:57` (загрузка `file`) — реализовать маршрутизацию.
- `Features/Cloud/AttachFile/AttachFileCommand.cs` + `Host/CloudApiService.cs` — пробросить флаг.
- `Shared/BarkCloud.Proto/files_api.proto` — `AttachFileRequest` += `bool route_by_media_kind`.

**Шаги:**
1. Новый enum `CloudDirectorySystemKind { None=0, Photos=1, Videos=2, OtherDocuments=3 }` + колонка на `CloudDirectory` (по умолчанию `None`). Миграция.
2. `EnsureSystemDirectory`: вернуть существующую системную папку владельца по `SystemKind` или создать (имя: «Фото»/«Видео»/«Другие документы», `ParentId = root`). Поиск по флагу, не по имени (устойчиво к переименованию).
3. proto: `AttachFileRequest { … bool route_by_media_kind = N; }`; прокинуть в `AttachFileCommand`.
4. В `AttachFileCommandHandler`: если `route_by_media_kind == true` → определить `SystemKind` по `file.MediaKind` (Photo→Photos, Video→Videos, иначе→OtherDocuments), `storageDirectoryId = EnsureSystemDirectory(...)`, игнорируя присланный `directory_id`. Иначе — текущее поведение (`directory_id`, пусто = корень).
5. Сервер «Недавно загруженные» не создаёт и на него не завязан (он был чисто клиентским). Существующие папки пользователей не трогаем.

**Проверка:** `dotnet build` зелёный; миграция применяется; юнит-тесты: (a) attach с `route_by_media_kind=true` для фото → попадает в системную «Фото» (создаётся при первом разе, переиспользуется при втором); (b) для видео → «Видео»; (c) для pdf → «Другие документы»; (d) attach с явным `directory_id` и `route_by_media_kind=false` → в указанную папку.

**Риски/заметки:** имена ru-строки, но матчинг по `SystemKind` (локализуемо позже). Удаление пользователем системной папки → следующий аплоад создаст заново (ensure) — защиту от удаления в v1 не вводим (минимализм). Клиентские правки (отправлять флаг, убрать «Недавно загруженные») — в Plan 03 (веб) и Plan 06 (iOS); Drive флаг не шлёт → грузит в свою папку.

---

## Задача 1.4 — `CheckFileHash`: чистая проверка + локация для модалок

**Цель:** `CheckFileHash` перестаёт «тихо» привязывать пользователя; возвращает факт наличия копии и где она лежит (имя+папка) — для модалок «такой файл уже есть».

**Файлы:**
- `Features/CheckFileHash/CheckFileHashCommandHandler.cs:53-64` — убрать побочный `AddUploaderToFile`.
- `Features/CheckFileHashes/CheckFileHashesCommandHandler.cs` — батч (уже без side-effect; добавить локацию опционально).
- `Persistence/ICloudHierarchyStorage.cs`/`CloudHierarchyStorage.cs` — `GetLiveEntriesForFile(ownerId, fileId)` (имя+папка существующей записи у пользователя).
- `Shared/BarkCloud.Proto/files_api.proto:236-238` — `CheckFileHashResponse`.

**Шаги:**
1. proto (аддитивно): `CheckFileHashResponse { string file_id = 1; bool exists = 2; repeated ExistingLocation existing_locations = 3; }`, где `ExistingLocation { string entry_id; string name; string directory_id; string directory_name; }`.
2. В обработчике: убрать `AddUploaderToFile`. Если блоб по хешу найден — заполнить `exists=true` и `existing_locations` живыми записями текущего пользователя (через `GetLiveEntriesForFile`). Если у пользователя записей нет (только чужой блоб/без attach) — `exists=true`, `existing_locations` пустой (клиент покажет «есть в облаке», без папки).
3. (Опц.) симметрично добавить локацию в `HashCheckResult` для `CheckFileHashes`.

**Проверка:** `dotnet build` зелёный; юнит-тест: после загрузки+attach файла в папку X, `CheckFileHash` по его хешу → `exists=true`, локация с именем и «X»; `AddUploaderToFile` не вызывается.

**Риски:** `CheckFileHash` зовут iOS/Drive — изменение аддитивное, старые поля сохранены; снятие side-effect меняет владение/квоту (это и нужно). Веб/iOS UI — отдельные планы.

---

## Задача 1.5 — Фикс бага «удаление файла не убирает из альбома»

**Цель:** при безвозвратном удалении файла (медиа без каталожной записи) чистить членство в альбомах и обложки.

**Файлы:**
- `Features/Cloud/DeleteUserMedia/DeleteUserMediaCommandHandler.cs:64-71` — ветка hard-delete (`RemoveUploaderFromFile` без чистки альбома).
- `Persistence/AlbumStorage.cs:129-144` (`RemoveItems`), `:90-104` (`GetItemCounts`).
- `Features/Album/RemoveItemsFromAlbum/RemoveItemsFromAlbumCommandHandler.cs:43-49` — эталон переустановки обложки.
- `Services/TrashPurgeService.cs:57-70` — эталон явной очистки `AlbumItems`.

**Шаги:**
1. Добавить `AlbumStorage.RemoveFileFromAllAlbums(ownerId, fileId)` (ExecuteDelete по `OwnerId+FileId`), возвращающий затронутые `AlbumId`.
2. В hard-delete ветке `DeleteUserMedia`: вызвать его; для альбомов с `CoverFileId == fileId` — переустановить обложку на первый оставшийся элемент (как в `RemoveItemsFromAlbum`) или сбросить в `null`. Делать в одном `SaveChanges`/транзакции.
3. **Подход — явная очистка, НЕ FK-каскад** (каскад не сработает: `UploadFile` живёт, пока `Uploaders` не пуст; см. `PLAN_00`).

**Подзадача 1.5b (бэкафилл) — ПРОПУЩЕНА** по решению пользователя (деструктивная авто-операция). Чинится только дальнейшая утечка; исторические сироты (если есть) остаются косметикой (счётчик/обложка) и исчезнут при штатной очистке блобов.

**Проверка:** `dotnet build` зелёный; юнит-тест: файл в альбоме (как обложка) → hard-delete → запись `AlbumItem` удалена, `items_count` корректен, обложка переустановлена/сброшена.

**Риски:** гонка с `AddItemsToAlbum` — операции в одной транзакции. Миграции не нужны (только данные/логика).

---

## Задача 1.6 — Синхронизация proto/web-прокси + финальная сборка

**Цель:** контракт и веб-прокси готовы к клиентским планам; всё собирается и тесты зелёные.

**Файлы:**
- `Shared/BarkCloud.Proto/files_api.proto` — итоговые аддитивные правки (1.3 `route_by_media_kind`, 1.4 `CheckFileHashResponse`).
- `Backend/BarkCloud.Web/Endpoints/CloudApiEndpoints.cs` — `/files/check-hash` (отдавать `exists`+локацию), `/cloud/attach` (принимать/пробрасывать `route_by_media_kind`), `/files/upload` (убрать неактуальный комментарий про «fileId мог измениться при дедупликации» — он больше не меняется).
- `Host/CloudApiService.cs` / `Host/FilesController.cs` — проброс новых полей.

**Шаги:**
1. Сгенерировать/проверить C#-типы из proto (сборка покрывает codegen). НЕ трогать `Android/.../proto/files_api.proto`.
2. Веб-прокси: прокинуть новые поля в JSON, чтобы React (Plan 03) мог их использовать. UI не делаем.
3. Полная сборка `dotnet build BarkCloud.slnx` + `dotnet test` (Files).

**Проверка:** решение собирается целиком; тесты Files зелёные.

**Финальный коммит плана** после 1.6.

---

## Обновление памяти проекта

По правилам vault: при изменении функциональности обновить заметки `Obsidian/BarkCloudVault/modules/backend-files.md`, `modules/backend-files-cloud.md`, `api/files-api.md` (снятие дедупа, системные папки + `route_by_media_kind`, чистый `CheckFileHash`, фикс альбома). Changelog не ведём.

## Что НЕ входит в Plan 01 (следующие планы)

- Веб/iOS UI модалок дубликатов, отправка `route_by_media_kind`, удаление «Недавно загруженные» на клиентах → Plan 03 / Plan 06.
- Шаринг (публичная страница, гранты, «мне доступны») → Plan 02 / Plan 04.
- Windows Drive верификация → Plan 05.
