# Files API — практический гайд клиента

Parent: [[Backend/Files]]
See also: [[Backend/ResumableUpload]] · [[Backend/FilesCloud]] · [[Backend/DynamicFolders]] · [[Backend/ImagePlaceholders]]

## Назначение

Практические шаги клиента для загрузки, просмотра галереи и работы с облачными папками. Полные серверные детали находятся в профильных заметках; источник контрактов — `Shared/BarkCloud.Proto/files_api.proto` (`package barkcloud.files`, C# namespace `BarkCloud.Proto.Files`).

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Shared/BarkCloud.Proto/files_api.proto` | Files-сервисы и сообщения | gRPC-контракт |
| `Backend/BarkCloud.Files/Host/FilesApiService.cs` | `FilesApiService` | RPC управления загрузкой |
| `Backend/BarkCloud.Files/Host/FilesController.cs` | upload/download routes | HTTP-контракт передачи байтов |
| `Backend/BarkCloud.Files/Features/Cloud/AttachFile/AttachFileCommandHandler.cs` | `AttachFile` | требования к привязке в облако |
| `Backend/BarkCloud.Files/Host/CloudApiService.cs` | `CloudApiService` | дерево и галерея |

## Публичные контракты

### Загрузка

Для resumable-передачи вызовите `FilesApi.CreateUploadSession` с уникальным для операции `idempotency_key`, именем, размером, MIME-типом и SHA-256. Сервер вернёт `session_id`, `file_id`, состояние и размер части.

1. Отправляйте части по `PUT /file-upload/{session_id}/parts/{part_number}`.
2. Для каждой части передайте `Content-Type: application/octet-stream`, точный `Content-Range: bytes start-end/total` и `X-Upload-Token` из ответа.
3. При прерывании вызовите `ResumeUploadSession`: используйте новый токен и список `uploaded_parts`; отсутствующие части и части с `has_etag=false` отправьте повторно.
4. Вызовите `CompleteUploadSession`, затем опрашивайте `GetUploadSession`, пока статус не станет `READY`.
5. Для появления файла в облачной папке вызовите `CloudApi.AttachFile` с `file_id`.

Полный протокол, расчёт размеров частей и состояния — [[Backend/ResumableUpload]].

Старый клиентский маршрут: `FilesApi.GetUploadUrl` возвращает URL внутреннего Files `POST /upload/{uploadId}`. Загрузите multipart-поле `file` по этому URL; это не S3 presigned URL.

### Скачивание и карточка файла

- Для собственных файлов запрашивайте `FilesApi.GetTempDownloadUrl(file_ids)`; используйте возвращённые `file_urls[].url` и `preview_url`, не конструируйте URL самостоятельно. Чужой ID отклоняет весь пакет. Для файла, которым поделились с пользователем, используйте `CloudApi.GetSharedFileDownloadUrl`.
- `UploadFileInfo.previews` содержит размеры и URL отдельных миниатюр. `preview_url` верхнего уровня deprecated.
- Для полного просмотра JPEG-варианта используйте `jpeg_view_url`; оригинал скачивается отдельно по обычной download-ссылке.
- `GetFileMetadata(file_id)` для готового собственного файла возвращает `has_metadata`; показывайте только заданные optional поля.

### Галерея

- Используйте `CloudApi.ListUserMedia` с `kind=PHOTO` или `VIDEO`; продолжайте выдачу парой `(next_cursor_created_at, next_cursor_file_id)`.
- `ListUserImages` помечен deprecated.
- Действие удаления из галереи — `DeleteUserMedia(file_id)`; API принимает ID блоба, а не ID записи папки.
- `placeholder.colors` и `placeholder.aspect_ratio` можно передать карточке до декодирования превью — [[Backend/ImagePlaceholders]].

### Облачные папки и альбомы

- Пустой `directory_id` в запросах дерева означает корень, если не задан `route_by_media_kind=true`. `ListDirectory` возвращает метаданные, `ListDirectoryDetailed` также содержит `UploadFileInfo`.
- `file_id` обозначает загруженный блоб; `entry_id` — запись этого блоба в дереве. Для переименования, перемещения и удаления записи используйте `entry_id`; для `AttachFile` — `file_id`.
- `AttachFile` вызывайте только после статуса `READY`. Параметр `route_by_media_kind=true` позволяет серверу выбрать системную папку по типу файла.
- Фото и видео добавляются в альбомы через `AlbumApi.AddItemsToAlbum`; страница альбома использует `ListAlbums` и `ListAlbumItems`. Курсор альбомов — пара `(cursor_updated_at, cursor_album_id)`, элементов — `(cursor_added_at, cursor_file_id)`.

## Ограничения и важные детали

- `CheckFileHash` и `CheckFileHashes` — read-only проверки SHA-256 для UX дубликатов; ответ включает `exists` и `existing_locations`. Совпадающее содержимое не объединяет блобы, и отдельная загрузка создаст отдельный файл.
- `FileNotReadyException` означает, что обработка ещё не завершена; повторите чтение состояния сессии. `FileAlreadyAttachedException` означает, что у владельца уже есть живая запись этого файла в дереве.
- Обновление критериев и получение содержимого умных папок описаны в [[Backend/DynamicFolders]].
