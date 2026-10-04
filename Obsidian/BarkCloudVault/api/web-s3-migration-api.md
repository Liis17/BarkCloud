# Web API — миграция S3

Parent: [[modules/web-s3-migration]] · See also: [[modules/web-settings]] · [[api/configuration-api]] · [[api/files-api]]

Все маршруты требуют обычную сессию и AdminGate: 401 без сессии, 403 без административной разблокировки. Credentials принимает только POST check; статусы и списки возвращают адреса, роли, ID и счётчики без ключей. Байты сериализуются строками Int64 для точного отображения через BigInt.

| Метод и путь | Назначение |
|---|---|
| GET `/api/settings/migration/sources` | Физические исходные группы, включая legacy и прошлые версии |
| POST `/api/settings/migration/check` | `{sourceId,destination:{serviceUrl,bucketName,accessKey,secretKey,region,forcePathStyle,isR2}}` → `{validationId,message}` |
| POST `/api/settings/migration/start` | `{validationId}` → серверная задача с проверенным подключением |
| GET `/api/settings/migration/jobs` | Состояния текущих временных задач |
| GET `/api/settings/migration/jobs/{id}` | Состояние одной задачи |
| POST `/api/settings/migration/jobs/{id}/retry` | Повторить незавершённый объект или переключение |
| POST `/api/settings/migration/jobs/{id}/cancel` | Остановить копирование или безопасно отменить финальный этап до сохранения |
| POST `/api/settings/server/storage/migration/apply` | `{jobId}`; preflight, фоновые drain/freeze/final sync, atomic relocate и restart Files |
| GET `/api/settings/migration/cutovers` | Долговечные незавершённые барьеры Files, включая потерянные после рестарта Web |
| POST `/api/settings/migration/cutovers/{id}/cancel` | Отмена после повторной проверки всей исходной группы Configuration |
| POST `/api/settings/migration/cutovers/{id}/recover` | Фоновый повтор перезапуска при уже сохранённых новых подключениях |

`check` разрешает существующие объекты назначения, проверяя LIST и тестовую запись/чтение/удаление. `start` сначала проверяет совпадающие ключи по размеру, metadata/заголовкам и SHA-256, затем копирует отсутствующие или отличающиеся объекты. После рестарта Web можно снова выполнить check/start с тем же бакетом, без очистки завершённых копий. Credentials по-прежнему вводятся заново и хранятся только в памяти.

Job содержит `state`, `phase`, source/destination, `totalBytes`, `copiedBytes`, `currentBytes`, total/copied files, полный `currentKey`, `currentName`, безопасную ошибку, activeUploads и флаги действий. Подтверждённые совпавшие копии входят в copiedBytes/copiedFiles без повторной загрузки. State: queued, running, stopping, copied, failed, cancelled, completed. Phase: counting, checking-existing, reading, copying, verifying, copied, draining, final-counting/final-reading/final-copying/final-verifying, applying, restarting, completed. Сообщения S3-ошибок могут включать HTTP-статус и код из фиксированного безопасного списка; raw SDK message не возвращается.
