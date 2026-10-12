# Backend — Files: облачная иерархия

Parent: [[Backend/Files]]
See also: [[Backend/OriginalLifetime]] · [[Backend/DynamicFolders]] · [[Api/FilesClientGuide]]

## Назначение

Иерархия файлов — PostgreSQL-дерево папок и записей, которые ссылаются на блобы `UploadFile`. Сами объекты хранятся отдельно в S3; эта модель задаёт имена, расположение, корзину и пользовательский доступ.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Files/Domain/CloudDirectory.cs` | `CloudDirectory` | Папка и ссылка на родителя |
| `Backend/BarkCloud.Files/Domain/CloudFileEntry.cs` | `CloudFileEntry` | Запись о блобе в дереве |
| `Backend/BarkCloud.Files/Persistence/CloudHierarchyStorage.cs` | `CloudHierarchyStorage` | Чтение и изменение дерева |
| `Backend/BarkCloud.Files/Persistence/FilesContext.cs` | индексы и FK | Уникальность и целостность ссылок |
| `Backend/BarkCloud.Files/Persistence/CloudTreeLocks.cs` | `LockCloudTreesAsync` | PostgreSQL advisory locks по владельцам |
| `Backend/BarkCloud.Files/Services/TrashPurgeService.cs` | окончательная очистка | Освобождение владельца и удаление сирот |
| `Shared/BarkCloud.Proto/files_api.proto` | `CloudApi` | gRPC-контракт |

## Публичные контракты

`CloudApi` требует пользовательскую авторизацию. Основные группы RPC:

| Группа | RPC |
|---|---|
| Дерево | `CreateDirectory`, `RenameDirectory`, `MoveDirectory`, `DeleteDirectory`, `ListDirectory`, `ListDirectoryDetailed`, `GetPath`, `SearchFiles` |
| Свойства и видимость | `GetDirectoryProperties`, `SetDirectoryVisibility` |
| Записи | `AttachFile`, `RenameFileEntry`, `MoveFileEntry`, `DeleteFileEntry`, `DeleteFileEntries` |
| Галерея | `ListUserMedia`, `GetUserMediaStats`, `DeleteUserMedia`, `SetVideoThumbnail` |
| Корзина | `ListTrash`, `GetTrashSummary`, `RestoreFromTrash`, `DeleteFromTrash`, `EmptyTrash` |
| Избранное и ссылки | `AddFavorite`, `RemoveFavorite`, `ListFavorites`, `CreateShare`, `ListMyShares`, `RevokeShare` |
| Дополнительные данные | `CreateArchive`, `ListFileActivity`, `GetMemories`, `ListMediaLocations` |
| Публичные папки/альбомы | `CreateFolderShare`, `ListMyFolderShares`, `RevokeFolderShare`; соответствующие RPC `CreateAlbumShare`, `ListMyAlbumShares`, `RevokeAlbumShare` |
| Доступ пользователям | Файлы: `ShareFileWithUser`, `RevokeUserShare`, `ListMyOutgoingShares`, `ListMyOutgoingSharesAll`, `ListSharedWithMe`, `GetSharedFileDownloadUrl`; папки: `ShareFolderWithUser`, `RevokeFolderUserShare`, `ListMyOutgoingFolderShares`, `ListSharedFoldersWithMe`, `ListSharedDirectory` |

Полный контракт с сообщениями находится в `Shared/BarkCloud.Proto/files_api.proto`. Практические шаги клиента собраны в [[Api/FilesClientGuide]].

### Видимость папок

`CloudDirectory.HideFromGallery` и `HideFromSearch` — собственные настройки папки; `EffectiveHideFromGallery` и `EffectiveHideFromSearch` — итоговые значения с учётом предков. Для каждого признака действует правило:

```text
effective = (InheritVisibility && ParentId != null) ? parent.effective : own
```

Корневая папка владельца не материализована и всегда видима. Верхнеуровневая папка (`ParentId=null`) не наследует виртуальный корень. При сохранении её видимости сервер устанавливает `InheritVisibility = !(HideFromGallery || HideFromSearch)`: если включено хотя бы одно скрытие, папка сохранит собственные настройки при переносе; если оба флага выключены, она сможет унаследовать настройки нового родителя.

`SetDirectoryVisibility` и `MoveDirectory` выполняют изменение и пересчёт поддерева сверху вниз под `CloudHierarchyStorage.LockTree`. `CreateDirectory` также использует этот lock и инициализирует итоговые флаги новой папки значениями родителя. `DirectoryVisibility.RecomputeSubtreeAsync` пересчитывает effective-флаги перемещённого поддерева и поддерева после смены настроек.

`GetDirectoryProperties` возвращает собственные и итоговые флаги, наличие родителя, а также количество живых записей, вложенных папок и суммарный размер живых файлов во всём поддереве. Записи корзины в эти счётчики не входят.

`HideFromGallery` исключает файл из `ListUserMedia`, `GetUserMediaStats`, устаревшего `ListUserImages`, `GetMemories`, `ListMediaLocations` и `MusicApi.ListTracks`. `SearchFiles` применяет этот фильтр только при `exclude_hidden_from_gallery=true` (веб передаёт флаг для поиска фото и видео). `SearchApi.Search` использует `HideFromSearch` для принадлежащих владельцу файловых секций и обычных результатов-папок.

Остальные пути сохраняют содержимое: `ListDirectory*`, умные папки, альбомы и их элементы, плейлисты и их треки, `ListSharedDirectory`, корзина и `ResolveHit`. Секция Shared глобального поиска также не использует настройки видимости владельца. В том числе скрытые файлы остаются доступными в альбомах и плейлистах.

## Ограничения и важные детали

- `CloudDirectory.ParentId=null` представляет корень; запись корневого файла хранит `DirectoryId=Guid.Empty`. В RPC пустой `directory_id` также означает корень.
- `CloudFileEntry` связывает `OwnerId`, папку и `FileId`; её `Name` — отображаемое имя в дереве, оно не меняет имя/метаданные блоба. Идентификатор записи (`entry_id`) отличается от идентификатора блоба (`file_id`).
- Корневая папка владельца не материализуется. Системные папки помечены `CloudDirectory.SystemKind`: фото, видео, музыка и прочие документы. `AttachFile.route_by_media_kind` направляет файл в одну из них.
- PostgreSQL обеспечивает уникальность имени папки в пределах владельца и родителя, а также имени живой записи в папке. Частичный уникальный индекс `(OwnerId, FileId)` с `IsDeleted=false` не допускает двух живых записей одного блоба у владельца; записи корзины не участвуют в этом индексе.
- `AttachFile` принимает только готовый файл, загруженный этим владельцем. При уже существующей живой записи повторная привязка отклоняется. Конфликт имени разрешается суффиксом.
- Удаление записи — мягкое: она получает `IsDeleted`, `DeletedAt` и `PurgeAt`. Блоб и владение сохраняются до окончательной очистки. Срок хранения в корзине — 14 дней.
- `DeleteUserMedia(file_id)` переносит все живые записи этого блоба в корзину. Если записей ещё не было, создаёт запись сразу в корзине, чтобы удаление оставалось восстановимым.
- `DeleteFromTrash`, `EmptyTrash` и фоновый `TrashCleanupService` передают записи в `TrashPurgeService`; окончательное удаление блоба и инварианты владения описаны в [[Backend/OriginalLifetime]].
- `ListUserImages` в proto помечен deprecated; для фото и видео предназначен `ListUserMedia`. Листинг использует пару курсора `(cursor_created_at, cursor_file_id)`.
- Умные папки рассчитываются отдельно и не являются каталогами дерева — [[Backend/DynamicFolders]].

## Зависимости и взаимодействия

Дерево хранится через EF Core в PostgreSQL. Операции записи используют `CloudHierarchyStorage.LockTree`: транзакция и advisory lock владельца сериализуют структурные изменения. S3 вызывается при окончательной очистке блобов, а не при переименовании и перемещении записей.
