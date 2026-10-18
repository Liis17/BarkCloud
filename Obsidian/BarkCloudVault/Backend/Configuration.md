# Backend — Configuration

Parent: [[Index]] · See also: [[Backend/Identity]], [[Backend/Notification]], [[Platform/Infrastructure]]

## Назначение

Централизованная БД настроек и служебный gRPC API, через который сервисы получают конфигурацию, зарезервированные имена, версии S3-профилей и группы квот S3.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Configuration/Catalog/SettingsCatalog.cs` | `SettingsCatalog` | Белый список ключей, типы значений и restart targets |
| `Backend/BarkCloud.Configuration/Infrastructure/ConfigurationStorage.cs` | `ConfigurationStorage` | Чтение, запись, история и проекции |
| `Backend/BarkCloud.Configuration/Infrastructure/StorageProfileStorage.cs` | `StorageProfileStorage` | Версии и активация S3-профилей |
| `Backend/BarkCloud.Configuration/Infrastructure/StorageQuotaGroupStorage.cs` | `StorageQuotaGroupStorage` | Группы квот и членство физических бакетов |
| `Backend/BarkCloud.Configuration/Infrastructure/ConfigurationDefaultsPopulator.cs` | `ConfigurationDefaultsPopulator` | Заполнение начальных значений |
| `Backend/BarkCloud.Configuration/Host/ConfigurationApiService.cs` | `ConfigurationApiService` | gRPC-реализация |
| `Backend/BarkCloud.Configuration/Program.cs` | `Main` | Запуск сервиса с общим logging lifecycle |
| `Shared/BarkCloud.Proto/configuration_api.proto` | `ConfigurationApi` | gRPC-контракт |
| `Backend/BarkCloud.GrpcServer/WebApplicationBuilderExtensions.cs` | `LoadConfiguration` | Загрузка настроек потребителями |

## Публичные контракты

`ConfigurationApi` предоставляет группы RPC:

| Контракт | Поведение |
|---|---|
| `GetReadiness` | Внутренний bootstrap RPC: подтверждает завершение стартовой инициализации настроек |
| `GetConfiguration`, `UpdateConfiguration` | Получить overlay сервиса или изменить существующий ключ каталога |
| `GetReservedNames`, `AddReservedName`, `UpdateReservedName`, `DeleteReservedName` | Управлять зарезервированными именами |
| `GetAllConfigurations`, `GetConfigurationHistory`, `RollbackConfiguration` | Административное чтение и откат ревизий |
| `GetStorageProfiles`, `SaveStorageProfile`, `ActivateStorageProfile`, `DisableStorageRole` | Читать и изменять версионируемые S3-профили |
| `SaveStorageQuotaGroup`, `DeleteStorageQuotaGroup` | Создавать, изменять и удалять группы квот S3 |
| `RelocateStorageProfiles` | Переназначить перечисленные профили на другое подключение |

Все вызовы защищены `x-config-access-key`. Вне Development `CONFIGURATION_ACCESS_KEY` обязателен при запуске; без ключа в Development interceptor пропускает вызов и пишет предупреждение. Bootstrap-ключ нужен потому, что сервис раздаёт секреты при старте, когда JWT-аутентификация потребителя ещё не настроена.

`GetReadiness` принимает пустой запрос и возвращает `ready=true`. `Program` запускает listener только после успешных миграций, `EnsureSeedAsync` и `PopulateDefaultsAsync`; до этого RPC недоступен. Ошибка заполнения настроек прерывает запуск, а существующие повторы при ошибках подключения к PostgreSQL сохраняются. Готовность подтверждает завершение инициализации, а не текущее состояние БД или готовность остальных сервисов.

`GetConfiguration(service_id)` объединяет global scope с настройками сервиса; значение сервиса перекрывает global по `Section:Key`. Ответ также содержит `ReservedNames:Usernames` как CSV-проекцию. Для Files добавляются совместимые `S3Buckets:*` проекции. `Features:EmailEnabled` вычисляется по наличию SMTP host, port, sender email и sender password; в таблицу настроек не записывается.

Профиль содержит стабильный `ProfileId`, роль, версию, подключение, credentials, `Region`, `ForcePathStyle`, `IsActive` и `IsLegacy`; собственной квоты у профиля нет. История хранится отдельно. Роли заданы в `StorageProfileRoles`: `universal`, `avatars`, `images`, `videos`, `audio`, `documents`, `other`, `previews` и совместимые `user-avatars-old`, `cloud-files-old`. `migration_id` позволяет безопасно повторить `RelocateStorageProfiles` после потерянного ответа, если целевые профили совпадают с уже выполненным переносом.

`StorageQuotaGroup` содержит стабильный `Id`, имя, `QuotaBytes` и список физических бакетов. Бакет определяется парой `S3Endpoint.Normalize(serviceUrl, isR2)` + `bucketName`; пара может входить только в одну группу. `GetStorageProfiles` возвращает группы вместе с профилями. Новые члены группы должны соответствовать существующим профилям; ранее сохранённый член разрешено оставить без профиля доступа, чтобы Files показывал неполный учёт. Квота хранится в байтах; ноль означает безлимит.

## Данные и взаимодействия

- Отдельные key/value-таблицы: `GlobalSettings`, `IdentitySettings`, `UsersSettings`, `NotificationSettings`, `FilesSettings`, `WebSettings`, `TorrentSettings`.
- `SettingsHistory` хранит изменения ключей; `ReservedNames` — нормализованные имена.
- `StorageProfiles` и `StorageProfileRevisions` хранят версии подключений и их ревизии; `StorageQuotaGroups` и `StorageQuotaGroupBuckets` — группы и их бакеты. В `StorageProfiles` столбца квоты нет.
- На старте `EnsureSeedAsync` добавляет недостающие строки каталога, а `PopulateDefaultsAsync` заполняет только пустые значения. Непустые значения оператора не перезаписываются.
- Identity, Users, Files, Torrent, Notification и Web получают снимок через `LoadConfigurationAsync(ServiceId)` и `CONFIGURATION_SERVICE_URL`, предварительно опрашивая `GetReadiness` по правилам [[Backend/GrpcServer]]. Обновления не рассылаются потребителям. Identity отдельно перечитывает `Features:RegistrationEnabled` при запросе регистрации.
- Валидация принимает только ключи каталога; Boolean нормализуются, URL ограничены абсолютными HTTP(S), порты — `1..65535`, другие integer-значения должны быть положительными. `JwtSettings:SecretKey` при изменении требует не менее 32 UTF-8 байт.

## Ограничения и важные детали

Собственная БД и порт сервиса задаются bootstrap-переменными `CONFIGURATION_HOST`, `CONFIGURATION_DBPORT`, `CONFIGURATION_DATABASE`, `CONFIGURATION_USERNAME`, `CONFIGURATION_PASSWORD` и `CONFIGURATION_PORT`. SMTP-поля могут оставаться пустыми; тогда `Features:EmailEnabled=false`. Вне Development обязательны внешние адреса Identity, Users, Files и Torrent.

Configuration использует общий с остальными backend-приложениями logging lifecycle из `BarkCloud.GrpcServer`. Локальные `ConsoleOutput`, `SeqEnable` и `LognaraEnable` задаются до запуска и не перекрываются remote overlay; Lognara подключается отдельным sink к loopback HTTP endpoint внутри того же контейнера. Агент не захватывает stdout/stderr и не читает файловые sources. Его запуск и остановка принадлежат общему lifecycle; настройки и ограничения описаны в [[Backend/GrpcServer]].

При развёртывании сначала обновляется Configuration с поддержкой `GetReadiness`, затем потребители. Старые потребители продолжают использовать прежние RPC; новые со старым Configuration завершают запуск с ошибкой `Unimplemented` без повторов.
