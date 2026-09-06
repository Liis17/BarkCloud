# Shared — Proto

Parent: [[index]]

## Назначение

Единственный источник правды по gRPC-контрактам. Содержит `.proto`-файлы для всех сервисов и общие типы. Используется и Backend (.NET), и потенциально Android-клиентом для генерации Kotlin-стабов.

## Расположение

`Shared/BarkCloud.Proto/`

## Файлы

| Файл | Содержимое | Связанная заметка |
|------|-----------|-------------------|
| `configuration_api.proto` | `ConfigurationApi` (служебный, между сервисами) | [[api/configuration-api]] |
| `identity_api.proto` | `IdentityApi`, `IdentityServerApi` | [[api/identity-api]] |
| `users_api.proto` | `UsersApi`, `UsersServerApi` | [[api/users-api]] |
| `files_api.proto` | `FilesApi`, `CloudApi`, `FilesServerApi` | [[api/files-api]] |
| `shared.proto` | Общие messages, типы и enum'ы, используемые в нескольких сервисах | — |

## Соглашения

- `csharp_namespace` для каждого файла: `BarkCloud.Proto.<Service>` (например, `BarkCloud.Proto.Files`)
- `package`: `barkcloud.<service>` (например, `barkcloud.identity`)
- Обычно два gRPC-сервиса на microservice: `XxxApi` (клиентский) и `XxxServerApi` (серверный/админ). В `files_api.proto` дополнительно выделен `CloudApi` для иерархии папок ([[modules/backend-files-cloud]]).
- Импорт общих типов через `import "shared.proto"`
- Используется `google.protobuf.Timestamp`

## Зависимости

- Используется: всеми Backend-микросервисами (зависимость в их `.csproj`) и Shared.Queue/Shared.Exceptions при необходимости

## Поисковые контракты

`files_api.proto` содержит `SearchApi`: `Search` (секции с отдельными limit/cursor), `ResolveHit`, `GetFileSearchMetadata`, `ReplaceFileSearchMetadata`; общие типы — `SearchSection`, `SearchHitKind`, `SearchHit`. `torrent_api.proto` содержит cursor-RPC `SearchTorrents`. Оба сервиса требуют пользовательский JWT.
