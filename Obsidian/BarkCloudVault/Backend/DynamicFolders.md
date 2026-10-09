# Backend — динамические папки

Parent: [[Backend/Files]]
See also: [[Backend/FilesCloud]] · [[Api/FilesClientGuide]]

## Назначение

Динамическая папка — виртуальная выборка файлов владельца по набору критериев. Содержимое вычисляется при чтении и не материализуется как отдельные записи каталога; один файл может попадать в несколько папок.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Files/Domain/DynamicFolder.cs` | `DynamicFolder` | Настройки пользовательской папки |
| `Backend/BarkCloud.Files/Domain/DynamicFolderCriteria.cs` | `DynamicFolderCriteria` | Комбинатор и правила в JSONB |
| `Backend/BarkCloud.Files/Domain/SystemDynamicFolders.cs` | системные ключи | Виртуальные предопределённые папки |
| `Backend/BarkCloud.Files/Persistence/DynamicFolderQueryBuilder.cs` | построение запроса | Критерии поверх выборки файлов владельца |
| `Backend/BarkCloud.Files/Persistence/DynamicFolderStorage.cs` | duplicate queries | Группировка дублей по хешу |
| `Backend/BarkCloud.Files/Host/DynamicFolderApiService.cs` | `DynamicFolderApiService` | gRPC API |
| `Shared/BarkCloud.Proto/files_api.proto` | `DynamicFolderApi`, enum и сообщения | Контракт |

## Публичные контракты

| RPC | Поведение |
|---|---|
| `CreateDynamicFolder`, `UpdateDynamicFolder`, `DeleteDynamicFolder` | Изменяют пользовательские правила и настройки |
| `ListDynamicFolders` | Возвращает системные папки первыми, затем пользовательские; включает count и cover |
| `ListDynamicFolderItems` | Страница файлов по `(cursor_created_at, cursor_file_id)`, с необязательным `kind_filter` |

`DynamicFolderInfo.id` системной папки — строковый ключ `sys-*`, пользовательской — GUID. Системные папки нельзя обновлять или удалять. Содержимое возвращается как `UserImageItem`, включая доступные `entry_ids`/`entry_names`; папки дубликатов также передают `duplicate_group_key`.

Поля и операторы правил задаются в `Shared/BarkCloud.Proto/files_api.proto` через `DfField`, `DfOperator`, `DfCombinator` и `DfViewMode`.

## Ограничения и важные детали

Базовая выборка включает готовые облачные блобы владельца, исключает превью и файлы, у которых остались только записи в корзине. Пустой набор корректных правил не добавляет фильтр к этой базовой выборке.

| Ключ системы | Содержимое |
|---|---|
| `sys-recent-media` | Фото и видео за последние 3 дня |
| `sys-recent-docs` | Документы, аудио и прочие файлы за последние 3 дня |
| `sys-large` | Файлы размером больше 100 MiB |
| `sys-screenshots` | Имена, содержащие `screenshot` без учёта регистра |
| `sys-duplicate-media` | Группы одинакового SHA-256 среди фото и видео |
| `sys-duplicate-files` | Группы одинакового SHA-256 среди документов, аудио и прочих файлов без фото/видео |

Правило может проверять дату загрузки или `FileMetadata.TakenAt`, размер, имя, тип медиа, расширение, размеры изображения, имя устройства загрузки либо `CameraMake + CameraModel`. Правила объединяются через «все» (`All`) или «любое» (`Any`); доступные операторы включают сравнение дат и чисел, равенство, подстроку, префикс и суффикс.

## Зависимости и взаимодействия

Папки используют `UploadFile`, `FileMetadata`, `FileHash` и `CloudFileEntry` для вычисления результатов и отображения действий над исходными записями. Настройки хранятся в PostgreSQL; контракт обслуживает Files.
