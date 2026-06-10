[[modules/backend-files]]

# Умные (динамические) папки

Виртуальные коллекции файлов, **собираемые автоматически по критериям** (в отличие от альбомов, куда файлы добавляют вручную). Один файл может попадать в несколько умных папок. Реализованы в backend сервиса Files + веб-клиент + **iOS-клиент** (таб «Файлы», см. [[modules/ios-app]]). Контракт общий (proto).

## Архитектурные решения

- **Содержимое не материализуется** — нет junction-таблицы; вычисляется на лету из критериев при каждом листинге/подсчёте.
- **Системные папки виртуальные** — не хранятся в БД, отдаются кодом с well-known id `sys-recent-media` / `sys-recent-docs` / `sys-large` / `sys-screenshots` / `sys-duplicate-media` / `sys-duplicate-files`. Критерии захардкожены в `Domain/SystemDynamicFolders.cs`. Нельзя редактировать/удалять.
  - «Недавние фото и видео» — `CreatedAt` за последние **3 дня** И `MediaKind ∈ {фото, видео}`; вид — сетка.
  - «Недавние документы» — `CreatedAt` за последние **3 дня** И `MediaKind = документ`; вид — список.
  - «Большие файлы» — `Size > 100 МБ` (104857600 байт).
  - «Скриншоты» — имя содержит `screenshot` (регистронезависимо).
  - «Дубликаты фото и видео» — живые фото/видео владельца, чей SHA-256 из `FileHashes.Hash` встречается более одного раза среди его живых файлов; вид — сетка, UI группирует по хешу.
  - «Дубликаты файлов» — живые документы/аудио/прочие файлы владельца, чей SHA-256 из `FileHashes.Hash` встречается более одного раза среди его живых файлов этих типов; вид — список, UI группирует по хешу.
- **Критерии — `jsonb`-колонка** через `ValueConverter<DynamicFolderCriteria,string>` (System.Text.Json) + `ValueComparer` в `FilesContext`. В SQL внутрь JSON не запрашиваем.
- Регистронезависимость по имени — через `Filename.ToLower().Contains/EndsWith` (стиль `ListUserImagesPage`).

## Ключевые файлы (backend)

- `Domain/` — `DynamicFolder`, `DynamicFolderCriteria`, `DynamicFolderRule`, enum'ы `DfField`/`DfOperator`/`DfCombinator`, `SystemDynamicFolders`.
- `Persistence/DynamicFolderQueryBuilder.cs` — транслятор критериев → `IQueryable<UploadFile>` поверх базового фильтра (как `ListUserMediaPage`, но без ограничения по `MediaKind`; видит все типы). Комбинирование И/ИЛИ через локальный `PredicateBuilder`. Метод `IsRuleValid` — валидация без БД.
- `Persistence/{IDynamicFolderStorage, DynamicFolderStorage}.cs` — CRUD + `CountByCriteria` / `ListItemsPage` (cursor) / `GetFirstItem` (обложка), а также duplicate-запросы `CountDuplicateItems` / `ListDuplicateItemsPage` / `GetFirstDuplicateItem` по `FileHashes.Hash`.
- `Features/DynamicFolder/{Create,Update,Delete,ListDynamicFolders,ListDynamicFolderItems}/` — CQRS (MediatR), по образцу `Features/Album`.
- `Services/DynamicFolderViewBuilder.cs` — батч count + обложка (превью 512 первого файла).
- `Mapping/DynamicFolderMapping.cs`, `Host/DynamicFolderApiService.cs` (защита `sys-*` от изменения).
- Миграция `AddDynamicFolders` (таблица `DynamicFolders`, колонка `Criteria jsonb`, индексы `(OwnerId,Name)` unique, `(OwnerId,SortOrder)`).

## Поля критериев

Дата загрузки, дата съёмки (`FileMetadata.TakenAt`), размер, имя, формат (`MediaKind`), расширение, ширина/высота изображения, устройство загрузки. Операторы: за последние N дней / до / после / больше / меньше / содержит / равно / начинается / заканчивается.

- Числовые поля (размер, ширина, высота) поддерживают **равно** наравне с больше/меньше (см. `TryBuildRule`).
- Правило `MediaKind` принимает **набор** кодов через запятую (`"1,2"` = фото или видео) — это позволяет одним правилом выразить «фото или видео» (используется системной папкой «Недавние фото и видео»). Парсинг — `ParseMediaKinds`.

### Режим отображения

`DynamicFolder.ViewMode` (enum `DfViewMode`: `Grid` / `List`) — хранится отдельной колонкой `ViewMode` (миграция `AddDynamicFolderViewMode`, default 0 = сетка), не входит в jsonb-критерии. Задаётся при создании/обновлении. Системные папки задают его в коде. На UI определяет рендер содержимого (сетка превью или список строк).

## API (proto `files_api.proto` → `DynamicFolderApi`)

`CreateDynamicFolder`, `UpdateDynamicFolder`, `DeleteDynamicFolder`, `ListDynamicFolders` (системные первыми + пользовательские), `ListDynamicFolderItems` (содержимое по критериям, cursor). Запросы Create/Update/Info несут `view_mode` (enum `DfViewMode`). `ListDynamicFolderItems` отдаёт `UserImageItem` (файл + записи каталога владельца: `entryIds`/`entryNames`) — нужно фронту для переименования/удаления/«показать в папке». Для системных папок дубликатов `UserImageItem.duplicate_group_key` содержит SHA-256 группы. См. [[api/files-api]].

## Веб

- Эндпоинты `/api/dynamic-folders[...]` в `Endpoints/CloudApiEndpoints.cs`, маппер `CloudJson.DynamicFolder`, gRPC-клиент в `Program.cs`.
- React: `components/dynamic-folders/` — `DynamicFoldersStrip` (горизонтальная лента, 2 ряда), `DynamicFolderCard` (квадратная плитка), `DynamicFolderFormModal` (конструктор правил + И/ИЛИ + переключатель сетка/список), `DynamicFolderDetail` (просмотр на месте: рендер сеткой или списком по `viewMode`, ПКМ-меню через `useMediaActions`; для `sys-duplicate-*` грузит все страницы и группирует по `duplicateGroupKey`, каждая группа рисуется отдельным контейнером: медиа — компактной сеткой, файлы — списком строк внутри рамки). В detail поддержан множественный выбор через `useSelection` + `SelectionBar`: выбранные элементы удаляются в корзину через `cloud/entry/delete` по `entryIds`, а для фото/видео без entry fallback — `cloud/media/delete`. Врезка в `pages/FilesPage.tsx` между шапкой и списком файлов (туда же передаются `albums`/`reloadAlbums` для меню). CSS — `.dynamic-folders` / `.df-*` / `.df-list*` / `.df-dup-*` в `styles/shared.css`.
