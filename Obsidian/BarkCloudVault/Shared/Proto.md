# Shared — Proto

Parent: [[Index]] · See also: [[Architecture]], [[Backend/GrpcServer]]

## Назначение

Исходные Protocol Buffers контракты для gRPC-сервисов BarkCloud. Детали серверной реализации хранятся в доменных заметках.

## Источники и контракты

| Файл | Сервисы | Связанная заметка |
|---|---|---|
| `Shared/BarkCloud.Proto/configuration_api.proto` | `ConfigurationApi` | [[Backend/Configuration]] |
| `Shared/BarkCloud.Proto/identity_api.proto` | `IdentityApi`, `IdentityServerApi` | [[Backend/Identity]] |
| `Shared/BarkCloud.Proto/session_revocation_api.proto` | `SessionRevocationApi` | [[Backend/SessionRevocation]] |
| `Shared/BarkCloud.Proto/users_api.proto` | `UsersApi`, `UsersServerApi` | [[Backend/Users]] |
| `Shared/BarkCloud.Proto/files_api.proto` | `FilesApi`, `CloudApi`, `FilesServerApi`, `AlbumApi`, `MusicApi`, `SearchApi`, `DynamicFolderApi` | [[Backend/Files]] |
| `Shared/BarkCloud.Proto/torrent_api.proto` | `TorrentApi` | [[Backend/Torrent]] |
| `Shared/BarkCloud.Proto/shared.proto` | Общие protobuf-сообщения, включая `PageRequest` | — |

Каждый API задаёт `package barkcloud.*` и C# namespace `BarkCloud.Proto.*`; временные значения представлены `google.protobuf.Timestamp`.

## Генерация и взаимодействия

`Shared/BarkCloud.Proto/BarkCloud.Proto.csproj` не перечисляет `.proto` для генерации. Backend и Web подключают исходные файлы в собственных `.csproj` с нужным `GrpcServices` режимом. `Backend/BarkCloud.GrpcServer/BarkCloud.GrpcServer.csproj` генерирует `configuration_api.proto` и `session_revocation_api.proto` с `Both`; Identity, Users, Files, Torrent и Web компилируют клиентские/серверные стороны используемых ими API.

Контракты изменяются в `Shared/BarkCloud.Proto/*.proto`; C# типы генерируются из включений в проектах, а не отдельным protobuf binary package.
