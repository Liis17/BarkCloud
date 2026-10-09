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
| Записи | `AttachFile`, `RenameFileEntry`, `MoveFileEntry`, `DeleteFileEntry`, `DeleteFileEntries` |
| Галерея | `ListUserMedia`, `GetUserMediaStats`, `DeleteUserMedia`, `SetVideoThumbnail` |
| Корзина | `ListTrash`, `GetTrashSummary`, `RestoreFromTrash`, `DeleteFromTrash`, `EmptyTrash` |
| Избранное и ссылки | `AddFavorite`, `RemoveFavorite`, `ListFavorites`, `CreateShare`, `ListMyShares`, `RevokeShare` |
| Дополнительные данные | `CreateArchive`, `ListFileActivity`, `GetMemories`, `ListMediaLocations` |
| Публичные папки/альбомы | `CreateFolderShare`, `ListMyFolderShares`, `RevokeFolderShare`; соответствующие RPC `CreateAlbumShare`, `ListMyAlbumShares`, `RevokeAlbumShare` |
| Доступ пользователям | Файлы: `ShareFileWithUser`, `RevokeUserShare`, `ListMyOutgoingShares`, `ListMyOutgoingSharesAll`, `ListSharedWithMe`, `GetSharedFileDownloadUrl`; папки: `ShareFolderWithUser`, `RevokeFolderUserShare`, `ListMyOutgoingFolderShares`, `ListSharedFoldersWithMe`, `ListSharedDirectory` |

Полный контракт с сообщениями находится в `Shared/BarkCloud.Proto/files_api.proto`. Практические шаги клиента собраны в [[Api/FilesClientGuide]].

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
