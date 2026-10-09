# Backend — Configuration

Parent: [[Index]] · See also: [[Backend/Identity]], [[Backend/Notification]], [[Platform/Infrastructure]]

## Назначение

Централизованная БД настроек и служебный gRPC API, через который сервисы получают конфигурацию, зарезервированные имена и версии S3-профилей.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Configuration/Catalog/SettingsCatalog.cs` | `SettingsCatalog` | Белый список ключей, типы значений и restart targets |
| `Backend/BarkCloud.Configuration/Infrastructure/ConfigurationStorage.cs` | `ConfigurationStorage` | Чтение, запись, история и проекции |
| `Backend/BarkCloud.Configuration/Infrastructure/StorageProfileStorage.cs` | `StorageProfileStorage` | Версии и активация S3-профилей |
| `Backend/BarkCloud.Configuration/Infrastructure/ConfigurationDefaultsPopulator.cs` | `ConfigurationDefaultsPopulator` | Заполнение начальных значений |
| `Backend/BarkCloud.Configuration/Host/ConfigurationApiService.cs` | `ConfigurationApiService` | gRPC-реализация |
| `Shared/BarkCloud.Proto/configuration_api.proto` | `ConfigurationApi` | gRPC-контракт |
| `Backend/BarkCloud.GrpcServer/WebApplicationBuilderExtensions.cs` | `LoadConfiguration` | Загрузка настроек потребителями |

## Публичные контракты

`ConfigurationApi` предоставляет группы RPC:

| Контракт | Поведение |
|---|---|
| `GetConfiguration`, `UpdateConfiguration` | Получить overlay сервиса или изменить существующий ключ каталога |
| `GetReservedNames`, `AddReservedName`, `UpdateReservedName`, `DeleteReservedName` | Управлять зарезервированными именами |
| `GetAllConfigurations`, `GetConfigurationHistory`, `RollbackConfiguration` | Административное чтение и откат ревизий |
| `GetStorageProfiles`, `SaveStorageProfile`, `ActivateStorageProfile`, `DisableStorageRole` | Читать и изменять версионируемые S3-профили |
| `RelocateStorageProfiles` | Переназначить перечисленные профили на другое подключение |

Все вызовы защищены `x-config-access-key`. Вне Development `CONFIGURATION_ACCESS_KEY` обязателен при запуске; без ключа в Development interceptor пропускает вызов и пишет предупреждение. Bootstrap-ключ нужен потому, что сервис раздаёт секреты при старте, когда JWT-аутентификация потребителя ещё не настроена.

`GetConfiguration(service_id)` объединяет global scope с настройками сервиса; значение сервиса перекрывает global по `Section:Key`. Ответ также содержит `ReservedNames:Usernames` как CSV-проекцию. Для Files добавляются совместимые `S3Buckets:*` проекции. `Features:EmailEnabled` вычисляется по наличию SMTP host, port, sender email и sender password; в таблицу настроек не записывается.

Профиль содержит стабильный `ProfileId`, роль, версию, подключение, credentials, квоту в байтах, `Region`, `ForcePathStyle`, `IsActive` и `IsLegacy`. История хранится отдельно. Роли заданы в `StorageProfileRoles`: `universal`, `avatars`, `images`, `videos`, `audio`, `documents`, `other`, `previews` и совместимые `user-avatars-old`, `cloud-files-old`. `migration_id` позволяет безопасно повторить `RelocateStorageProfiles` после потерянного ответа, если целевые профили совпадают с уже выполненным переносом.

## Данные и взаимодействия

- Отдельные key/value-таблицы: `GlobalSettings`, `IdentitySettings`, `UsersSettings`, `NotificationSettings`, `FilesSettings`, `WebSettings`, `TorrentSettings`.
- `SettingsHistory` хранит изменения ключей; `ReservedNames` — нормализованные имена.
- `StorageProfiles` и `StorageProfileRevisions` хранят версии подключений и их ревизии.
- На старте `EnsureSeedAsync` добавляет недостающие строки каталога, а `PopulateDefaultsAsync` заполняет только пустые значения. Непустые значения оператора не перезаписываются.
- Большинство сервисов получают снимок через `LoadConfiguration(ServiceId)` и `CONFIGURATION_SERVICE_URL`; обновления не рассылаются потребителям. Identity отдельно перечитывает `Features:RegistrationEnabled` при запросе регистрации.
- Валидация принимает только ключи каталога; Boolean нормализуются, URL ограничены абсолютными HTTP(S), порты — `1..65535`, другие integer-значения должны быть положительными. `JwtSettings:SecretKey` при изменении требует не менее 32 UTF-8 байт.

## Ограничения и важные детали

Собственная БД и порт сервиса задаются bootstrap-переменными `CONFIGURATION_HOST`, `CONFIGURATION_DBPORT`, `CONFIGURATION_DATABASE`, `CONFIGURATION_USERNAME`, `CONFIGURATION_PASSWORD` и `CONFIGURATION_PORT`. SMTP-поля могут оставаться пустыми; тогда `Features:EmailEnabled=false`. Вне Development обязательны внешние адреса Identity, Users, Files и Torrent.
