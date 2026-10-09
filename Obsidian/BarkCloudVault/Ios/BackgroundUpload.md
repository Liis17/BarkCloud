# iOS — Фоновая загрузка

Parent: [[Ios/IosApp]]
See also: [[Ios/Widgets]], [[Backend/ResumableUpload]], [[Backend/Files]]

## Назначение

Файловые загрузки приложения, автозагрузка PhotoKit и Share Extension проходят через один resumable-процесс: состояние задания сохраняется в App Group, управляющие вызовы идут по gRPC, части файла отправляются через background `URLSession`.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Ios/BarkCloud/BarkCloud/Networking/BackgroundUploadCoordinator.swift` | `BackgroundUploadCoordinator` | Координация очереди, сессий и передачи частей |
| `Ios/BarkCloud/BarkCloud/Data/Cache/UploadJob.swift`, `Ios/BarkCloud/BarkCloud/Data/Cache/UploadQueueStore.swift` | `UploadSessionJob`, `UploadQueueStore` | Модель и персистентное состояние заданий |
| `Ios/BarkCloud/BarkCloud/Networking/UploadConstants.swift` | `UploadConstants`, очистка артефактов | App Group, URLSession, staging и миграция очереди |
| `Mac/BarkCloudKit/Sources/BarkCloudKit/Networking/FileTransferService.swift` | `FileTransferService` | gRPC control plane и построение HTTP URL частей |
| `Ios/BarkCloud/ShareExtension/ShareViewController.swift` | `ShareViewController` | Выбор папки и постановка вложений в очередь |
| `Ios/BarkCloud/BarkCloud/Features/Gallery/Backup/BackupManager.swift` | `BackupManager` | Автозагрузка медиатеки устройства |
| `Ios/BarkCloud/BarkCloud/App/AppDelegate.swift`, `Ios/BarkCloud/BarkCloud/Networking/UploadLiveActivityController.swift`, `Ios/BarkCloud/BarkCloud/Networking/UploadProgressObserver.swift` | восстановление задач и состояние UI | Подхват URLSession-событий, retry и отображение прогресса |

## Публичные контракты

- `CloudRepository.enqueueBackgroundUpload(...)` принимает файл, копирует его в staging, вычисляет размер и SHA-256 и создаёт задание. `enqueueAndWaitForReady(...)` возвращает `fileID` после серверного состояния `ready`.
- Поток задания: локальный файл → хеш и создание/возобновление серверной сессии → отправка отсутствующих частей background URLSession → завершение серверной сессии → действие после `ready`.
- Действие после готовности задаётся как `none`, `attachDirectory`, `routeByMediaKind` или `addToAlbum`. Если загрузка завершилась, но привязка не удалась, состояние `uploadedNotAttached` позволяет повторить только привязку.
- Источники заданий: ручная загрузка, Share Extension и резервное копирование фототеки. Состояние отображается через баннер приложения и Live Activity; детали интерфейса — в [[Ios/Widgets]].

## Зависимости и взаимодействия

- `UploadQueueStore` хранит задания в `UploadSessionQueue.sqlite` внутри App Group. Исходные файлы и временные файлы частей находятся в `UploadStaging`; копирование и SHA-256 используют буфер до 4 МиБ.
- `FileTransferService` выполняет управляющие операции с upload-сессией по gRPC; части передаются обычным HTTP через фоновую `URLSession`. Серверные ограничения и формат сессии описаны в [[Backend/ResumableUpload]].
- Share Extension использует тот же App Group, Keychain-сессию, очередь и идентификатор background `URLSession`. После выбора папки она ставит вложения в очередь; закрытие расширения не отменяет уже отправленные задачи.
- До завершения одноразовой миграции старой очереди расширение сохраняет вложения в `ShareInbox`; приложение позднее забирает их и ставит в новую очередь.
- `AppDelegate` принимает системный completion handler background URLSession и регистрирует `BGProcessingTask` для повторов. При старте и возвращении приложения в активное состояние координатор связывается с незавершёнными передачами.

## Ограничения и важные детали

- Координатор резервирует не более четырёх заданий одновременно, включая передачи background URLSession, пережившие перезапуск процесса. Размер части задаётся серверной сессией.
- Upload token не сохраняется в модели очереди; после запуска процесса клиент получает актуальный токен при возобновлении сессии. Три — максимальное число сетевых повторов задания.
- `uploadedNotAttached` остаётся активным заданием до успешной привязки или отмены. Временные данные очищаются после завершения/отмены; сиротские staging-файлы удаляются отдельной очисткой.
