# gRPC API — Configuration

Parent: [[index]] · Module: [[modules/backend-configuration]] · Proto: [[modules/shared-proto]]

Файл: `Shared/BarkCloud.Proto/configuration_api.proto`
Namespace C#: `BarkCloud.Proto.Configuration`
Package: `barkcloud.configuration`

## Сервис `ConfigurationApi`

Служебный API вызывается только серверными сервисами. Каждый RPC защищён bootstrap-заголовком `x-config-access-key` из `CONFIGURATION_ACCESS_KEY` (обязателен вне Development).

| RPC | Назначение |
|-----|-----------|
| `GetConfiguration` | Global + service overlay для одного потребителя; также compatibility-проекции и вычисляемый `Features:EmailEnabled` |
| `UpdateConfiguration` | Обновить существующий ключ строгого каталога |
| `GetReservedNames` / `AddReservedName` / `UpdateReservedName` / `DeleteReservedName` | CRUD нормализованных reserved names |
| `GetAllConfigurations` | Все settings-таблицы с metadata для админского Web |
| `GetConfigurationHistory` | История одного ключа |
| `RollbackConfiguration` | Транзакционный откат к `PreviousValue` выбранной ревизии |
| `GetStorageProfiles` | Все версии S3-профилей; optional audit metadata |
| `SaveStorageProfile` | Создание версии, legacy correction или credentials rotation |
| `ActivateStorageProfile` | Выбор версии для новых записей роли |
| `DisableStorageRole` | Отключение специализированной роли для новых записей |

Исходные номера старых RPC и полей сохранены; новые поля `ConfigurationItem` добавлены совместимо: `is_sensitive`, `has_value`, `is_read_only`, `value_kind`, `restart_targets`.

`StorageProfileItem` внутри доверенного service-to-service канала содержит credentials, нужные Files при старте. Web никогда не проксирует raw secret браузеру: [[modules/backend-web]] преобразует ответ в masked DTO.

Профиль также содержит `quota_bytes` (квота физического бакета в байтах). `SaveStorageProfile` принимает целое `quota_value` и `quota_unit` (`gb`, `tb`, `pb`); единицы бинарные, `0` означает безлимит. Files получает квоту при стартовой загрузке профилей. Квота синхронизируется для профилей с одинаковыми endpoint и bucket, а её изменение требует перезапуска Files.

## Использование

- Все сервисы вызывают `GetConfiguration` при старте через `LoadConfiguration` и `CONFIGURATION_SERVICE_URL`.
- Files дополнительно вызывает `GetStorageProfiles` и индексирует registry по `ProfileId`; live reload не используется.
- Старый Files-клиент получает `S3Buckets:user-avatars` и `S3Buckets:cloud-files` как проекцию legacy-профилей.
- Users пока получает `ReservedNames:Usernames` как CSV-проекцию новой таблицы.
