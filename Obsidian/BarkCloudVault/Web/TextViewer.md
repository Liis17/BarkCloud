# Web — просмотр текстовых файлов

Parent: [[Web/WebApp]] · See also: [[Backend/Files]]

## Назначение

SPA открывает поддерживаемые текстовые файлы из облака, общего доступа и публичных ссылок. Файл размером более 10 МиБ можно скачать, но нельзя просмотреть.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Web/Endpoints/TextPreviewEndpoints.cs` | `MapTextPreviewEndpoints` | HTTP-доставка содержимого с проверкой доступа |
| `Backend/BarkCloud.Web/Infrastructure/TextPreviewProxy.cs` | `TextPreviewProxy` | Ограниченный прокси к Files HTTP/1 |
| `Backend/BarkCloud.Web/ClientApp/src/lib/textFiles.ts` | `getTextFileKind`, `decodeTextFile` | Классификация и декодирование текста |
| `Backend/BarkCloud.Web/ClientApp/src/components/text/TextFileViewer.tsx` | `TextFileViewer` | Просмотр и скачивание оригинала |
| `Backend/BarkCloud.Web/ClientApp/src/components/text/MarkdownPreview.tsx` | `MarkdownPreview` | Безопасный рендер Markdown |

## Публичные контракты

| Метод и путь | Доступ |
|---|---|
| `GET /api/files/text?id=` | Авторизованный владелец; Web получает временный URL через Files |
| `GET /api/shared/text?fileId=` | Авторизованный пользователь с доступом к файлу |
| `GET /api/public/shares/{token}/text` | Публичная ссылка на файл |
| `GET /api/public/folder-shares/{token}/text?dir=&fileId=` | Публичная папка; файл должен входить в указанный каталог |

## Зависимости и взаимодействия

Web проверяет доступ через Files gRPC API, затем получает байты по внутреннему HTTP/1 endpoint Files. Прокси принимает только временный download URL с путём `/download/{guid}` и строит запрос на `FilesService:Http1Base`. Ответы не кэшируются; прокси устанавливает `X-Content-Type-Options: nosniff` и возвращает исходные байты без преобразования.

Просмотр поддерживает текстовые и исходные файлы, JSON и Markdown; HTML и SVG отображаются как текст. Автокодировка распознаёт BOM UTF-16LE/BE, иначе использует UTF-8. Доступны ручные UTF-8, UTF-16LE/BE и Windows-1251.

## Ограничения и важные детали

- Порог 10 МиБ проверяется и в браузере, и на сервере; сервер также учитывает фактический размер потока, даже если Content-Length отсутствует.
- Markdown пропускает сырой HTML; ссылки и изображения допускают только HTTP(S), а также mailto и якоря для ссылок. KaTeX работает с `trust: false`.
- Mermaid отключает HTML labels, использует `securityLevel: strict`, отвергает директивы конфигурации из файла и ограничивает размер схемы.
- Относительные пути изображений и ссылок внутри облачного хранилища не разрешаются.
