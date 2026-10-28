# Shared — Proto

Parent: [[Index]] · See also: [[Architecture]], [[Backend/GrpcServer]]

## Назначение

Исходные Protocol Buffers контракты для gRPC-сервисов BarkCloud. Детали серверной реализации хранятся в доменных заметках.

## Источники и контракты

| Файл | Сервисы | Связанная заметка |
|---|---|---|
| `Shared/BarkCloud.Proto/configuration_api.proto` | `ConfigurationApi` | [[Backend/Configuration]] |
| `Shared/BarkCloud.Proto/identity_api.proto` | `IdentityApi` | [[Backend/Identity]] |
| `Shared/BarkCloud.Proto/service_credentials_api.proto` | `ServiceCredentialsApi` | [[Backend/Identity]], [[Backend/GrpcServer]] |
| `Shared/BarkCloud.Proto/session_revocation_api.proto` | `SessionRevocationApi` | [[Backend/SessionRevocation]] |
| `Shared/BarkCloud.Proto/users_api.proto` | `UsersApi`, `UsersServerApi` | [[Backend/Users]] |
| `Shared/BarkCloud.Proto/files_api.proto` | `FilesApi`, `CloudApi`, `FilesServerApi`, `AlbumApi`, `MusicApi`, `SearchApi`, `DynamicFolderApi` | [[Backend/Files]] |
| `Shared/BarkCloud.Proto/directory_sizes_api.proto` | `DirectorySizeApi` (размеры подпапок из кеша) | [[Backend/FilesCloud]] |
| `Shared/BarkCloud.Proto/torrent_api.proto` | `TorrentApi` | [[Backend/Torrent]] |
| `Shared/BarkCloud.Proto/shared.proto` | Общие protobuf-сообщения, включая `PageRequest` | — |

Каждый API задаёт `package barkcloud.*` и C# namespace `BarkCloud.Proto.*`; временные значения представлены `google.protobuf.Timestamp`.

`IdentityApi` предоставляет отдельные `CreateAccountWithPassword` и `ConfirmAccountWithPassword` для регистрации с первоначальным паролем. Они используют request-типы `CreateAccountRequest` и `ConfirmAccountRequest` с optional `initial_password` (поля 5 и 3 соответственно); legacy `CreateAccount` и `ConfirmAccount` это поле игнорируют. Сервер проверяет пароль нового контракта до побочных эффектов.

`ConfirmResetPasswordRequest` содержит optional `reset_passkeys` (поле 6) и `revoke_other_sessions` (поле 4). При отсутствии каждого поля сервер считает выбор включённым.

`IdentityApi` добавил авторизованные `BeginReauthentication` и `CompleteReauthentication` для выдачи одноразового разрешения управления passkey. `BeginWebAuthnRegistrationRequest`, `CompleteWebAuthnRegistrationRequest` и `RemoveWebAuthnCredentialRequest` принимают additive optional `permission_token`; старые вызовы без него отклоняются сервером. Разрешение связано с действием и, при удалении, конкретным credential. Подробный поток описан в [[Backend/Identity]].

`FilesApi.CreateUploadSessionRequest.file_type` — optional enum на поле 6: без него сервер выбирает `CLOUD_FILE`; явные `CLOUD_FILE` и `USER_AVATAR` поддержаны, `UNKNOWN` и неизвестные значения отклоняются. Для аватара действует лимит 20 MiB.

## Генерация и взаимодействия

`Shared/BarkCloud.Proto/BarkCloud.Proto.csproj` не перечисляет `.proto` для генерации. Backend и Web подключают исходные файлы в собственных `.csproj` с нужным `GrpcServices` режимом. `Backend/BarkCloud.GrpcServer/BarkCloud.GrpcServer.csproj` генерирует `configuration_api.proto` и `session_revocation_api.proto` с `Both`; Identity, Users, Files, Torrent и Web компилируют клиентские/серверные стороны используемых ими API.

Контракты изменяются в `Shared/BarkCloud.Proto/*.proto`; C# типы генерируются из включений в проектах, а не отдельным protobuf binary package.
