# iOS — App

Parent: [[Index]]
See also: [[Ios/BackgroundUpload]], [[Ios/Widgets]], [[Ios/FileCache]], [[Backend/Files]], [[Backend/Users]], [[Shared/Proto]], [[Api/FilesClientGuide]], [[Api/UsersClientGuide]]

## Назначение

Нативный iOS-клиент BarkCloud на SwiftUI, минимальная версия — iOS 18. Приложение объединяет вход в аккаунт, медиатеку устройства, облачные файлы и медиа, общий доступ, настройки и локальное хранение данных.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Ios/BarkCloud/BarkCloud.xcodeproj/project.pbxproj` | `BarkCloud`, `ShareExtension`, `BarkCloudWidgets`, `BarkCloudTests` | Приложение и три дополнительных таргета; группы исходников синхронизируются с файловой системой |
| `Ios/BarkCloud/BarkCloud/App/BarkCloudApp.swift`, `Ios/BarkCloud/BarkCloud/App/AppEnvironment.swift`, `Ios/BarkCloud/BarkCloud/App/RootView.swift` | `AppEnvironment`, `RootView` | Создание сервисов и выбор начального экрана |
| `Ios/BarkCloud/BarkCloud/Features/Main/MainScreen.swift`, `Ios/BarkCloud/BarkCloud/Features/Main/MainDestination.swift` | `MainScreen`, `MainDestination` | Вкладки и переходы |
| `Mac/BarkCloudKit/Package.swift`, `Mac/BarkCloudKit/Sources/BarkCloudKit/Networking/GrpcManager.swift` | `BarkCloudKit`, `ServerConfig`, `GrpcManager`, `GrpcEndpoint` | Общий для iOS/macOS сетевой слой и адреса сервисов |
| `Mac/BarkCloudKit/Sources/BarkCloudKit/Session/SessionStore.swift`, `Mac/BarkCloudKit/Sources/BarkCloudKit/Networking/AuthInterceptor.swift` | `SessionStore`, `AuthInterceptor` | Хранение токенов и gRPC-аутентификация |
| `Mac/BarkCloudKit/Sources/BarkCloudKit/Data/Cloud/CloudRepository.swift`, `Mac/BarkCloudKit/Sources/BarkCloudKit/Data/Users/UserRepository.swift` | `CloudRepository`, `UserRepository` | Клиентские операции облака и учётной записи |
| `Ios/BarkCloud/BarkCloud/Features/AppLock/AppLockManager.swift`, `Ios/BarkCloud/BarkCloud/Data/Cache/AppLockSettings.swift`, `Ios/BarkCloud/BarkCloud/Features/AppLock/SetPinSheet.swift`, `Ios/BarkCloud/BarkCloud/Features/Vault/BiometricGate.swift`, `Ios/BarkCloud/BarkCloud/Features/Vault/VaultStore.swift` | блокировка и локальный сейф | Локальная защита и состояние сейфа |
| `Mac/BarkCloudKit/Sources/BarkCloudKit/Networking/GrpcManager.swift`, `Ios/BarkCloud/BarkCloud/Features/Shared/MySharesViewModel.swift`, `Ios/BarkCloud/BarkCloud/Features/Shared/SharePresenter.swift` | `GrpcEndpoint`, `MySharesViewModel`, `SharePresenter` | Создание URL публичных ссылок и их передача пользователю |

## Публичные контракты

| Контракт | Поведение |
|---|---|
| Навигация | Пять вкладок: «Галерея» (PhotoKit), «Файлы» (устройство, облако и общий доступ), «Альбомы» (облачные фото, видео и альбомы), «Корзина», «Настройки». Стартовая вкладка — «Альбомы» |
| Выбор сервера | До первого сохранения адресов показывается настройка сервера; далее приложение ведёт на вход или в основную часть в зависимости от сессии |
| Схема URL | `barkcloud://albums`, `barkcloud://trash`, `barkcloud://vault`, `barkcloud://media/<file-id>`; обработчик `DeepLink` маршрутизирует ссылки виджетов |
| Публичные ссылки | `CloudRepository` получает токены файлов, альбомов и папок; `GrpcEndpoint` формирует URL `/s/<token>`, `/al/<token>`, `/f/<token>`. `SharePresenter` копирует URL или открывает системный лист отправки |
| Доступ по приглашению | `Ios/BarkCloud/BarkCloud/Features/Shared/ShareWithUserSheet.swift` и `Ios/BarkCloud/BarkCloud/Features/Shared/OutgoingSharesSheet.swift` управляют доступом для пользователей отдельно от публичных ссылок |

## Зависимости и взаимодействия

- `AppEnvironment` связывает UI с репозиториями, передачей файлов, настройками, кэшем, блокировкой и фоновыми задачами.
- `BarkCloudKit` содержит gRPC-клиенты, сгенерированные типы, репозитории, `ServerConfig` и `SessionStore`. `GrpcManager` кэширует клиентов по адресу, подключает interceptor-ы токена и метаданных устройства/приложения; access-токен обновляется заранее, параллельные refresh-запросы объединяются.
- Адреса Identity, Users и Files, TLS и флаг допуска self-signed сертификата хранятся в App Group UserDefaults. `FilesApi`, `CloudApi`, `AlbumApi` и `DynamicFolderApi` используют Files endpoint.
- gRPC используется для управления и метаданных; байты файлов передаются отдельным HTTP-слоем `FileTransferService`. Контракты описаны в [[Shared/Proto]], серверная сторона — в [[Backend/Files]] и [[Backend/Users]].
- Ключевые локальные данные: сессия в Keychain; настройки конфигурации в App Group; кеш файлов в `FileCacheService`; настройки App Lock и список сейфа — локально.

## Ограничения и важные детали

- Если сохранена сессия и включён App Lock, `RootView` показывает блокировку после холодного старта и при возврате из фона спустя более 30 секунд. PIN хранится как PBKDF2-HMAC-SHA256 хеш с солью в Keychain; сравнение хеша выполняется за постоянное время. Биометрическая проверка использует `deviceOwnerAuthentication`, включая код-пароль устройства.
- После трёх неверных PIN блокировка стирает локальное состояние приложения, включая сессию, кеши, сейф и конфигурацию сервера.
- «Сейф» хранит локальный список идентификаторов в UserDefaults и скрывает эти элементы из обычной галереи. Сервер не знает о локальной защите; файлы остаются обычными облачными файлами.
- В iOS-проекте подключена общая библиотека `Mac/BarkCloudKit`; сетевые типы и сгенерированные gRPC-файлы не принадлежат каталогу исходников основного iOS-таргета.
