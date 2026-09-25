# Backend — Configuration

Parent: [[index]] · See also: [[api/configuration-api]] · [[structure/infrastructure]]

## Назначение

Центральный сервис настроек BarkCloud. Сохраняет package/service и имя проекта `BarkCloud.Configuration`, работает с отдельной БД `configuration` и раздаёт потребителям конфигурацию только при их старте. Live reload нет.

## Схема данных

- `GlobalSettings`, `IdentitySettings`, `UsersSettings`, `NotificationSettings`, `FilesSettings`, `WebSettings`, `TorrentSettings` — отдельные key/value-таблицы (`Key` PK, `Value`, `EditedBy`, `EditedAt`).
- `SettingsHistory` — предыдущие/новые значения, автор, источник, тип изменения и optional `SourceRevisionId`. Update и rollback идут в транзакции; в PostgreSQL строка блокируется `FOR UPDATE`.
- `ReservedNames` — нормализованные lowercase-имена, одно имя на строку. Для старого Users-клиента `GetConfiguration` формирует read-only CSV-проекцию `ReservedNames:Usernames`.
- `StorageProfiles` — версионируемые S3-профили со стабильным `ProfileId`; `StorageProfileRevisions` — их audit-журнал.
- `ConfigurationsLegacy` — прежняя таблица после автомиграции. Runtime её не читает и не изменяет; неизвестные legacy-строки остаются в ней и выводятся warning’ом.

## Каталог и чтение

`Catalog/SettingsCatalog.cs` содержит строгий каталог существующих ключей и метаданные UI: тип значения, sensitive/read-only и список контейнеров для перезапуска. API не создаёт неизвестные ключи. Общие значения читаются первыми, затем настройки сервиса перекрывают их по полному `Section:Key`. Редкие существующие service override для известных global-ключей сохраняются миграцией.

Typed validation ограничивает TCP-порты диапазоном `1..65535`, а длительности — положительными целыми. Источник последнего изменения для каждого ключа выбирается из `SettingsHistory` на стороне БД, без материализации всего append-only журнала.

`Features:EmailEnabled` не хранится: `GetConfigurationCommandHandler` вычисляет его по полноте четырёх SMTP-полей Notification. `Features:RegistrationEnabled` хранится в `GlobalSettings`; быстрый toggle Web использует тот же API обновления.

## Seed при старте

`ConfigurationDefaultsPopulator` на каждом старте:

- создаёт отсутствующие строки каталога;
- заполняет только пустые значения из env, литерала или одноразового генератора;
- не перезаписывает непустые/ручные значения;
- сохраняет сгенерированный JWT secret и межсервисные токены, поэтому следующие старты стабильны;
- оставляет SMTP пустым без `EMAIL_*`;
- не подставляет `minioadmin`, `guest` или фиксированные dev-секреты;
- создаёт только `universal-v1` (`cloud-universal`) при полном наборе `MINIO_*` и сразу пишет storage-revision.

Вне Development обязательны внешние `EXTERNAL_*_HOST` и `CONFIGURATION_ACCESS_KEY`. Отсутствие bootstrap access key останавливает сервис до подключения к БД; в Development остаётся warning-режим interceptor’а.

## Legacy-миграция

`20260908120000_RebuildConfigurationSettings`:

- переименовывает `Configurations` в `ConfigurationsLegacy`;
- при дублях выбирает максимальные `EditedAt`, затем `Id`;
- переносит известные строки и metadata в таблицу соответствующего сервиса;
- создаёт `Migration`-ревизии с прежним `EditedFrom`;
- разбирает CSV reserved names, приводит к lowercase и удаляет дубли;
- создаёт `user-avatars-old-v1` и `cloud-files-old-v1` из прежних `S3Buckets:*`, только если профиль полный;
- не копирует, не переименовывает и не удаляет S3-объекты.

Миграция транзакционна и идемпотентна через EF migrations history. EF snapshot синхронизирован; `dotnet ef migrations has-pending-model-changes` не находит расхождений.

## S3-профили

Роли: `universal`, `avatars`, `images`, `videos`, `audio`, `documents`, `other`, `previews`, а также compatibility-роли `user-avatars-old` и `cloud-files-old`.

- Смена endpoint, bucket или `IsR2` активного обычного профиля создаёт следующую версию и деактивирует старую.
- Ротация credentials обновляет все версии той же физической локации.
- Отключение специализированной роли прекращает новые записи; версии остаются доступными для чтения.
- Legacy-профиль никогда не активируется для новых объектов; любое исправление требует явного подтверждения.
- Пустой secret при редактировании сохраняет текущий; частичный профиль отклоняется.
- `QuotaBytes` хранит квоту физического бакета в байтах; ввод целого `quota_value` с `quota_unit` (`gb`/`tb`/`pb`) пересчитывается в бинарные единицы, `0` означает безлимит. При сохранении квота синхронизируется между профилями одной физической локации (`endpoint + bucket`); изменения существующих профилей записываются отдельными ревизиями `QuotaChange` и требуют перезапуска Files.

## Основные файлы

- `Infrastructure/ConfigurationContext.cs` — EF-модель всех новых таблиц.
- `Catalog/SettingsCatalog.cs`, `SettingsValueValidator.cs` — whitelist и typed validation.
- `Infrastructure/ConfigurationStorage.cs` — overlay, history/rollback, reserved names и compatibility-проекции.
- `Infrastructure/StorageProfileStorage.cs` — версии, активация, disable и credential rotation.
- `Domain/StorageProfileQuota.cs` — валидация целого значения и безопасное преобразование ГБ/ТБ/ПБ в байты.
- `Infrastructure/ConfigurationDefaultsPopulator.cs` — идемпотентный seed.
- `Infrastructure/ConfigurationAccessPolicy.cs` — production bootstrap-гейт.
- `Infrastructure/LegacyConfigurationReporter.cs` — предупреждения о неизвестных legacy-ключах.
- `Host/ConfigurationApiService.cs` — реализация [[api/configuration-api]].

## Окружение

Собственная БД и bootstrap-параметры не хранятся в settings-таблицах: `CONFIGURATION_HOST`, `CONFIGURATION_DBPORT`, `CONFIGURATION_DATABASE`, `CONFIGURATION_USERNAME`, `CONFIGURATION_PASSWORD`, `CONFIGURATION_PORT`, `CONFIGURATION_ACCESS_KEY`, `ASPNETCORE_ENVIRONMENT`.

Автозаполнение использует `POSTGRES_*`, `RABBITMQ_DEFAULT_*`, `MINIO_HOST/MINIO_PORT/MINIO_ROOT_USER/MINIO_ROOT_PASSWORD`, optional `EMAIL_*`, обязательные production `EXTERNAL_*_HOST` и service ports из compose.
