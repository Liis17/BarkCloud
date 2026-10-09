# Цветовые плейсхолдеры превью

Parent: [[Backend/Files]]
See also: [[Backend/ResumableUpload]] · [[Web/WebApp]] · [[Shared/Proto]]

## Назначение

Files вычисляет цвета для временной карточки фото или видео, пока клиент загружает и декодирует основное изображение.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Files/Domain/FilePlaceholder.cs` | `FilePlaceholder` | Сохранённые цвета и источник |
| `Backend/BarkCloud.Files/Services/ImagePlaceholderSampler.cs` | `SampleAsync` | Расчёт девяти цветов |
| `Backend/BarkCloud.Files/Services/FilePlaceholderService.cs` | `EnsureAsync` | Выбор превью и сохранение результата |
| `Backend/BarkCloud.Files/Services/FilePlaceholderBackfillService.cs` | `FilePlaceholderBackfillService` | Фоновая обработка готовых файлов |
| `Backend/BarkCloud.Files/Services/PreviewPersistenceService.cs` | pipeline превью | Вызов расчёта после генерации |
| `Shared/BarkCloud.Proto/files_api.proto` | `FilePlaceholderInfo` | Поля клиентского DTO |

## Публичные контракты

`FilePlaceholderInfo` содержит `colors` — девять значений `#RRGGBB` слева направо и сверху вниз, и `aspect_ratio`. Он передаётся в `UploadFileInfo.placeholder=19` и `SearchHit.placeholder=15`. Отсутствующий placeholder означает, что данные ещё не подготовлены.

## Ограничения и важные детали

- `FilePlaceholders` хранит `FileId` оригинала, массив цветов, `AspectRatio` и `SourceFilePreviewId`, связанный с записью `FilePreview`.
- Источник — самое маленькое готовое обычное превью; для фото без обычного превью используется JPEG-вариант просмотра с `TargetWidth=0`. Оригинал для расчёта не скачивается.
- `ImagePlaceholderSampler` применяет EXIF-ориентацию. Для фото семплируется центральная квадратная область, для видео — вся миниатюра. В центрах сетки 3×3 усредняются RGB-каналы области 5×5 пикселей.
- Новые превью проходят через `PreviewPersistenceService`. Ошибка сохранения цветов логируется и не переводит успешную загрузку в ошибку.
- `FilePlaceholderBackfillService` находит готовые облачные фото/видео без цветов порциями по 200 и повторяет проходы через пять минут; обычные preview blobs исключены.

## Зависимости и взаимодействия

Расчёт использует ImageSharp и байты превью. Поля доступны в Files API и поиске; Web использует их для клиентского плейсхолдера — [[Web/WebApp]].
