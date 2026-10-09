# macOS Drive — папка облака в Finder

Parent: [[Architecture]] · См. также: [[Desktop/WindowsDrive]] · [[Ios/IosApp]] · [[Shared/Proto]]

## Назначение

Нативный macOS-клиент представляет облако BarkCloud как File Provider-домен в Finder. Контейнер-app `BarkCloud Drive` отвечает за настройку сервера, вход и управление доменом; расширение `BarkCloudFS` транслирует операции Finder в облачные операции через `BarkCloudKit`. Проект настроен на macOS 15.4 и новее.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Mac/BarkCloudDrive/BarkCloudDrive/BarkCloudDriveApp.swift` | `BarkCloudDriveApp`, `RootView` | Menu-bar приложение и выбор экрана |
| `Mac/BarkCloudDrive/BarkCloudDrive/AppModel.swift` | `AppModel` | Сессия, сервер, профиль и квота |
| `Mac/BarkCloudDrive/BarkCloudDrive/FileProviderDomainManager.swift` | `FileProviderDomainManager` | Регистрация, пауза и удаление домена |
| `Mac/BarkCloudDrive/BarkCloudFS/BarkCloudFileProvider.swift` | `BarkCloudFileProvider` | Реализация операций File Provider |
| `Mac/BarkCloudDrive/BarkCloudFS/BarkCloudFileProviderItem.swift` | `BarkCloudFileProviderItem` | Представление файла/каталога для Finder |
| `Mac/BarkCloudDrive/BarkCloudFS/BarkCloudEnumerator.swift`, `BarkCloudItemCache.swift` | enumerator и cache actor | Перечисление и сопоставление идентификаторов |
| `Mac/BarkCloudKit/Sources/BarkCloudKit/Networking/GrpcManager.swift`, `Data/Cloud/CloudRepository.swift` | `GrpcManager`, `CloudRepository` | Общий сетевой слой и файловый репозиторий |
| `Mac/BarkCloudDrive/BarkCloudWidgets/StorageWidget.swift` | `StorageWidget` | Виджет квоты |

## Публичные контракты

- Контейнер использует File Provider domain ID `com.barkfluff.BarkCloud.Drive.MainDomain` и отображаемое имя `BarkCloud`; домен регистрируется через `NSFileProviderManager`.
- Корень использует системный `.rootContainer`; каталоги и файлы имеют item identifiers `d:<directoryID>` и `f:<entryID>`. Версия содержимого файла привязана к `fileID`, метаданные — к имени и родительскому каталогу.
- Конфигурация Identity/Users/Files хранится в App Group UserDefaults, refresh/access-токены — в общем Keychain access group приложения и расширения.
- Виджет получает снимок квоты через App Group UserDefaults; контейнер обновляет его после загрузки данных профиля.

## Зависимости и взаимодействия

- Системный `fileproviderd` запускает `BarkCloudFS.appex` по запросам Finder; отдельного процесса движка, управляемого приложением, нет.
- Контейнер управляет состоянием домена: `enable()` добавляет новый или reconnect существующий домен; `disable()` временно disconnect'ит его; `purge()` удаляет домен. При выходе/смене сервера приложение очищает общую с расширением сессию и persistent cache.
- Расширение лениво создаёт `SessionStore`, `GrpcManager`, `FileTransferService` и `CloudRepository` из `BarkCloudKit`. Этот SwiftPM-пакет также подключён к iOS-таргетам.
- Enumerator получает содержимое каталога из cloud repository и записывает в JSON cache соответствия item identifier → cloud ID. Cache доступен через App Group, поэтому переживает перезапуск расширения.
- Имя в облаке и имя в Finder хранятся отдельно. Локальные имена очищаются от недопустимых символов и дедуплицируются с учётом регистра и Unicode-нормализации, не меняя облачное имя.
- File Provider поддерживает загрузку превью Finder для файлов с доступным preview URL. Системные файлы `.DS_Store`, `.localized` и `._*` не синхронизируются.

## Ограничения и важные детали

- Чтение через `fetchContents` материализует файл целиком через временную download URL. `RangeBlockReader` создаётся в сетевом контейнере, но этот путь чтения его не вызывает.
- `createItem` и изменение содержимого читают исходный файл целиком в `Data`; загрузка крупных файлов может потреблять память пропорционально размеру файла.
- Изменение содержимого заменяет запись: старый entry удаляется, новое содержимое загружается и прикрепляется как новый entry. При ошибке загрузки провайдер пытается восстановить старую запись из корзины.
- `workingSet` и trash перечисляются пустыми. Sync anchor увеличивается после локальных мутаций; отдельного журнала или потока внешних изменений нет, поэтому изменения от других клиентов требуют полного перечисления каталога.
- Для одного cloud file ID допускается одна живая запись. Одинаковое содержимое может иметь отдельные file ID; текущий backend сохраняет оригиналы без дедупликации. Модель описана в [[Backend/Files]].
- Причина выбора File Provider задокументирована в `Mac/README.md`: для FSKit требуется entitlement, недоступный Personal Apple Developer team; File Provider не требует этого entitlement. Таргеты не объявляют FSKit entitlement.
