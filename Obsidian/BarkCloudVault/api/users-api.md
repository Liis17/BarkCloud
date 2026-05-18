# gRPC API — Users

Parent: [[index]] · Module: [[modules/backend-users]] · Proto: [[modules/shared-proto]]

Файл: `Shared/BarkCloud.Proto/users_api.proto`
Namespace C#: `BarkCloud.Proto.Users`
Package: `barkcloud.users`

## Сервис: `UsersApi` (клиентский)

Все RPC из proto:

| RPC | Реализовано? |
|-----|--------------|
| `GetUser(GetUserRequest) → GetUserResponse` | ✅ |
| `SetProfilePicture(SetProfilePictureRequest) → SetProfilePictureResponse` | ✅ |
| `CheckExistUsername(CheckExistUsernameRequest) → CheckExistResponse` | ✅ |
| `CheckExistEmail(CheckExistEmailRequest) → CheckExistResponse` | ✅ |
| `ChangeName(ChangeNameRequest) → ChangeNameResponse` | ✅ |
| `ChangeUsername(ChangeUsernameRequest) → ChangeUsernameResponse` | ✅ |
| `GetDevices(GetDevicesRequest) → GetDevicesResponse` | ✅ |
| `GetCurrentDevice(GetCurrentDeviceRequest) → GetCurrentDeviceResponse` | ✅ |
| `RenameDevice(RenameDeviceRequest) → RenameDeviceResponse` | ✅ |

## Сервис: `UsersServerApi` (служебный)

| RPC | Реализовано? |
|-----|--------------|
| `FindByLogin(FindByLoginRequest) → FindByLoginResponse` | ✅ |
| `CheckExistUsername`, `CheckExistEmail` | ✅ (повторно объявлены и в server-варианте) |
| `AddDraftUser(AddDraftUserRequest) → AddDraftUserResponse` | ✅ |
| `OverrideDraftUser(AddDraftUserRequest) → AddDraftUserResponse` | ✅ |
| `ConfirmUser(ConfirmUserRequest) → ConfirmUserResponse` | ✅ |
| `GetById(GetByIdRequest) → GetByIdResponse` | ❌ объявлен, **нет handler-а** в `Features/` |
| `GetUserContacts(GetUserContactsRequest) → GetUserContactsResponse` | ✅ |
| `ListByIds(ListByIdsRequest) → ListByIdsResponse` | ✅ |
| `RegisterDevice(RegisterDeviceRequest) → RegisterDeviceResponse` | ✅ |
| `GetUserDevices(GetUserDevicesRequest) → GetUserDevicesResponse` | ✅ |
| `DeleteUserDevice(DeleteUserDeviceRequest) → DeleteUserDeviceResponse` | ✅ |
| `UpdateStorageLimit(UpdateStorageLimitRequest) → UpdateStorageLimitResponse` | ✅ |
| `SetProfilePictureServer(SetProfilePictureServerRequest) → SetProfilePictureServerResponse` | ✅ |
| `UpdateProfileServer(UpdateProfileServerRequest) → UpdateProfileServerResponse` | ✅ |

## Состояние домена

В proto и сервисе **отсутствуют**: ChangeBio, SearchUsers, Badges, ChatFolders, ExportData, Personalization, Prekeys, Privacy, SetFirebaseToken. Если/когда появятся — добавить в эту таблицу и [[modules/backend-users]].

## Типизированные ошибки

См. [[modules/shared-exceptions]] · Users:
- `UserIsDraftException`
- `ProfilePictureHasNotValidType`
- `BioTooLongException` (исключение объявлено, но фича ChangeBio пока не реализована)
- `ChatFolderInvalidNameException`, `ChatFolderNotFoundException` (для ChatFolders, которых пока нет)

## События

Через `UserInfoQueueSender.cs` сервис может публиковать в RabbitMQ события из [[modules/shared-queue]]: `UserChangedName`, `UserChangedUsername`, `UserChangedAvatar`. (Для `UserChangedBio`/`UserChangedPassword` DTO существуют, но соответствующие фичи в Users отсутствуют.)

Слушает: `SessionRevokedEvent`.
