# gRPC API — Users

Parent: [[index]] · Module: [[modules/backend-users]] · Proto: [[modules/shared-proto]]

Файл: `Shared/BarkCloud.Proto/users_api.proto`
Namespace C#: `BarkCloud.Proto.Users`
Package: `barkcloud.users`

> Клиентский гайд (что слать / что вернётся по каждому эндпоинту): [[api/users-client-guide]].

## Сервис: `UsersApi` (клиентский)

Все RPC из proto:

| RPC | Реализовано? |
|-----|--------------|
| `GetUser(GetUserRequest) → GetUserResponse` | ✅ |
| `SetProfilePicture(SetProfilePictureRequest) → SetProfilePictureResponse` | ✅ |
| `CheckExistUsername(CheckExistUsernameRequest) → CheckExistResponse` | ✅ (AllowAnonymous) |
| `CheckExistEmail(CheckExistEmailRequest) → CheckExistResponse` | ✅ (AllowAnonymous) |
| `ChangeName(ChangeNameRequest) → ChangeNameResponse` | ✅ |
| `ChangeUsername(ChangeUsernameRequest) → ChangeUsernameResponse` | ✅ |
| `ChangeBio(ChangeBioRequest) → ChangeBioResponse` | ✅ |
| `SearchUsers(SearchUsersRequest) → SearchUsersResponse` | ✅ |
| `DeleteAccount(DeleteAccountRequest) → DeleteAccountResponse` | ✅ |
| `GetPrivacySettings(GetPrivacySettingsRequest) → GetPrivacySettingsResponse` | ✅ |
| `UpdatePrivacySettings(UpdatePrivacySettingsRequest) → UpdatePrivacySettingsResponse` | ✅ |
| `GetDevices(GetDevicesRequest) → GetDevicesResponse` | ✅ |
| `GetCurrentDevice(GetCurrentDeviceRequest) → GetCurrentDeviceResponse` | ✅ |
| `RenameDevice(RenameDeviceRequest) → RenameDeviceResponse` | ✅ |
| `DeleteDevice(DeleteDeviceRequest) → DeleteDeviceResponse` | ✅ |
| `SetFirebaseToken(SetFirebaseTokenRequest) → SetFirebaseTokenResponse` | ✅ |

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

Реализованы: ChangeBio, SearchUsers, DeleteAccount, Privacy (Get/Update), DeleteDevice (клиентский), SetFirebaseToken. **Отсутствуют**: Badges, ChatFolders, ExportData, Personalization, Prekeys, SearchUsersServer. Смена пароля — в [[modules/backend-identity]] (`SetPassword`), не в Users.

## Типизированные ошибки

См. [[modules/shared-exceptions]] · Users:
- `UserIsDraftException`
- `ProfilePictureHasNotValidType` — `SetProfilePicture` с файлом не типа `USER_AVATAR`
- `BioTooLongException` — `ChangeBio` с bio > 200 символов
- `UsernameReservedException` (Identity) — `ChangeUsername` с зарезервированным именем
- `UserNotFoundException` (Identity) — пользователь не найден
- `ChatFolderInvalidNameException`, `ChatFolderNotFoundException` (для ChatFolders, которых пока нет)

## События

Через `UserInfoQueueSender.cs` публикуются в RabbitMQ ([[modules/shared-queue]]): `UserChangedName`, `UserChangedUsername`, `UserChangedAvatar`, `UserChangedBio`, `UserDeleted`. DTO `UserChangedPassword` остаётся в shared-queue, но из Users не публикуется (пароли — в Identity). `UserDeleted` слушают консьюмеры в Identity (отзыв сессий + чистка пароля/2FA/сбросов/кодов) и Files (открепление блобов + удаление каталогов/альбомов).

Слушает: `SessionRevokedEvent`.
