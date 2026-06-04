[[modules/backend-files]]

# Умные (динамические) папки

Виртуальные коллекции файлов, **собираемые автоматически по критериям** (в отличие от альбомов, куда файлы добавляют вручную). Один файл может попадать в несколько умных папок. Реализованы в backend сервиса Files + веб-клиент. Контракт общий (proto), мобильные клиенты получат API позже.

## Архитектурные решения

- **Содержимое не материализуется** — нет junction-таблицы; вычисляется на лету из критериев при каждом листинге/подсчёте.
- **Системные папки виртуальные** — не хранятся в БД, отдаются кодом с well-known id `sys-recent` / `sys-large` / `sys-screenshots`. Критерии захардкожены в `Domain/SystemDynamicFolders.cs`. Нельзя редактировать/удалять.
  - «Недавно загруженные» — `CreatedAt` за последние **3 дня**.
  - «Большие файлы» — `Size > 100 МБ` (104857600 байт).
  - «Скриншоты» — имя содержит `screenshot` (регистронезависимо).
- **Критерии — `jsonb`-колонка** через `ValueConverter<DynamicFolderCriteria,string>` (System.Text.Json) + `ValueComparer` в `FilesContext`. В SQL внутрь JSON не запрашиваем.
- Регистронезависимость по имени — через `Filename.ToLower().Contains/EndsWith` (стиль `ListUserImagesPage`).

## Ключевые файлы (backend)

- `Domain/` — `DynamicFolder`, `DynamicFolderCriteria`, `DynamicFolderRule`, enum'ы `DfField`/`DfOperator`/`DfCombinator`, `SystemDynamicFolders`.
- `Persistence/DynamicFolderQueryBuilder.cs` — транслятор критериев → `IQueryable<UploadFile>` поверх базового фильтра (как `ListUserMediaPage`, но без ограничения по `MediaKind`; видит все типы). Комбинирование И/ИЛИ через локальный `PredicateBuilder`. Метод `IsRuleValid` — валидация без БД.
- `Persistence/{IDynamicFolderStorage, DynamicFolderStorage}.cs` — CRUD + `CountByCriteria` / `ListItemsPage` (cursor) / `GetFirstItem` (обложка).
- `Features/DynamicFolder/{Create,Update,Delete,ListDynamicFolders,ListDynamicFolderItems}/` — CQRS (MediatR), по образцу `Features/Album`.
- `Services/DynamicFolderViewBuilder.cs` — батч count + обложка (превью 512 первого файла).
- `Mapping/DynamicFolderMapping.cs`, `Host/DynamicFolderApiService.cs` (защита `sys-*` от изменения).
- Миграция `AddDynamicFolders` (таблица `DynamicFolders`, колонка `Criteria jsonb`, индексы `(OwnerId,Name)` unique, `(OwnerId,SortOrder)`).

## Поля критериев

Дата загрузки, дата съёмки (`FileMetadata.TakenAt`), размер, имя, формат (`MediaKind`), расширение, ширина/высота изображения, устройство загрузки. Операторы: за последние N дней / до / после / больше / меньше / содержит / равно / начинается / заканчивается.

## API (proto `files_api.proto` → `DynamicFolderApi`)

`CreateDynamicFolder`, `UpdateDynamicFolder`, `DeleteDynamicFolder`, `ListDynamicFolders` (системные первыми + пользовательские), `ListDynamicFolderItems` (содержимое по критериям, cursor). См. [[api/files-api]].

## Веб

- Эндпоинты `/api/dynamic-folders[...]` в `Endpoints/CloudApiEndpoints.cs`, маппер `CloudJson.DynamicFolder`, gRPC-клиент в `Program.cs`.
- React: `components/dynamic-folders/` — `DynamicFoldersStrip` (горизонтальная лента, 2 ряда), `DynamicFolderCard` (квадратная плитка), `DynamicFolderFormModal` (конструктор правил + И/ИЛИ), `DynamicFolderDetail` (просмотр на месте). Врезка в `pages/FilesPage.tsx` между шапкой и списком файлов. CSS — `.dynamic-folders` / `.df-*` в `styles/shared.css`.
