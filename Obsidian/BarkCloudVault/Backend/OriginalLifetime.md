# Жизненный цикл оригинала и облачных ссылок

Parent: [[Backend/Files]]
See also: [[Backend/FilesCloud]] · [[Backend/ResumableUpload]]

## Назначение

Описывает границу между ссылками облачного дерева (`CloudFileEntry`) и физическим блобом (`UploadFile`): привязку, корзину, снятие владения и удаление из S3.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Files/Features/Cloud/AttachFile/AttachFileCommandHandler.cs` | `AttachFileCommandHandler` | Проверяет готовность и владение перед созданием ссылки |
| `Backend/BarkCloud.Files/Features/Cloud/DeleteUserMedia/DeleteUserMediaCommandHandler.cs` | `DeleteUserMediaCommandHandler` | Перемещает записи в корзину |
| `Backend/BarkCloud.Files/Services/TrashPurgeService.cs` | `TrashPurgeService` | Окончательно удаляет записи и освобождает блобы |
| `Backend/BarkCloud.Files/Services/OrphanBlobCleanupService.cs` | `OrphanBlobCleanupService` | Повторяет очистку осиротевших объектов |
| `Backend/BarkCloud.Files/Persistence/FileOwnership.cs` | атомарное изменение `Uploaders` | Конкурентные изменения владения |
| `Backend/BarkCloud.Files/Persistence/FilesContext.cs` | связь и индекс | `Restrict` и индекс по `FileId` |

## Ограничения и важные детали

- `CloudFileEntry.FileId` ссылается на существующий `UploadFile.Id` через FK с поведением `Restrict`; очистка оригинала не каскадно удаляет записи дерева.
- `AttachFile` начинает транзакцию и блокирует дерево владельца до чтения файла и создания ссылки. Привязать можно только готовый блоб, в чьём списке `Uploaders` есть владелец.
- `DeleteUserMedia` использует блокировку того же дерева. Живые записи перемещаются в корзину; если записей нет, создаётся удалённая запись, чтобы дать возможность восстановить операцию.
- `TrashPurgeService` повторно проверяет состояние корзины в транзакции. Ссылка удаляется только если всё ещё находится в корзине и, для фонового удаления, срок `PurgeAt` истёк.
- Владелец блоба снимается только при отсутствии других записей этого владельца, включая записи в корзине. Альбомы, избранное, ссылки и гранты очищаются вместе с последней записью.
- Превью могут разделяться несколькими оригиналами. Владелец снимается с превью, только если на него не ссылается другой оригинал этого владельца.
- После освобождения владельца S3-объект удаляется, только если у него пустой `Uploaders` и нет ни одной `CloudFileEntry`. Операция S3 идёт в транзакционном захвате сироты; при ошибке транзакция откатывается, и фоновая очистка сможет повторить попытку.
- PostgreSQL advisory locks дерева берутся до блокировок строк по возрастанию `OwnerId`; строки оригиналов обновляются в порядке `FileId`, а строки общих превью блокируются перед освобождением.

## Зависимости и взаимодействия

Ручное удаление навсегда, очистка корзины и `TrashCleanupService` используют одну службу `TrashPurgeService`. Модель корзины и API находятся в [[Backend/FilesCloud]], а состояние загрузки описано в [[Backend/ResumableUpload]].
