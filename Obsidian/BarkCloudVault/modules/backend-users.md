# Backend — Users

Parent: [[index]] · See also: [[api/users-api]] · [[modules/shared-queue]]

## Назначение

Сервис пользователей: профили (имя, юзернейм, bio), draft-flow регистрации, устройства (+ push-токен Firebase), контакты, аватарка, настройки приватности, поиск, удаление аккаунта, лимиты хранилища, проверки уникальности.

> Клиентский гайд по эндпоинтам (что слать / что вернётся): [[api/users-client-guide]].

## Расположение

`Backend/BarkCloud.Users/`

## Файлы

### Domain
- `User.cs` — основная сущность пользователя (+ `Bio`, навигация `Privacy`)
- `UserDevice.cs` — устройство (поля: `Id`, `UserId`, `OriginalName`, `CustomName`, `AuthorizedAt`, `AppName`, `OperationSystem`, `Location`, `FirebaseToken`)
- `UserContact.cs` — контакт
- `UserPrivacy.cs` — настройки приватности (one-to-one с User: `ProfileVisibility`, `EmailVisibility`, `LastSeenVisibility`, `SearchableByUsername`)
- `PrivacyVisibility.cs` — enum (`Everyone`/`Contacts`/`Nobody`)

> **Не реализовано в Domain**: Badge, ChatFolder, DevicePrekeyBundle, OneTimePrekey, ProfileFieldVisibility, UserPersonalization, UserBadge. Если появятся — обновить эту заметку.

### Host (gRPC)
- `UsersApiService.cs` — клиентский `UsersApi`
- `UsersServerApiService.cs` — серверный `UsersServerApi`

### Services
- `ReservedUsernamesService.cs` — берёт список зарезервированных юзернеймов из [[modules/backend-configuration]]

### Helpers
- `PasswordHasher.cs` — локальный хешер паролей (см. также `BarkCloud.Shared.SecurityUtilities`)

### Mapping
- `UserMapping.cs` — маппинг Domain ↔ Proto

### Infrastructure
- `UserInfoQueueSender.cs` — публикует события `UserChanged*` в RabbitMQ ([[modules/shared-queue]])

### Consumers
- `SessionRevokedConsumer.cs`

### Persistence
- `Contexts/UsersContext.cs`, `UsersContextFactory.cs`
- `Services/UsersStorage.cs` (+ `ChangeBio`, `SearchUsers`, `DeleteUser`, `GetOrCreatePrivacy`, `UpdatePrivacy`)
- `Services/DevicesStorage.cs` (+ `SetFirebaseToken`)
- `Migrations/20260518171439_InitialCreate.cs`, `20260524215052_AddBioPrivacyFirebaseToken.cs`, `20260602120000_AddUserLookupIndexes.cs` (raw-SQL индексы производительности: функциональные `lower("Username")`/`lower("Email")` под точный логин + триграммные GIN `pg_trgm` на `lower(Username/FirstName/LastName)` под подстрочный `SearchUsers` — ранее seq-scan)

## Features (реализованные)

### Профиль
- `GetUser` — query
- `SetProfilePicture`, `SetProfilePictureServer`
- `ChangeName`, `ChangeUsername`, `ChangeBio`
- `SearchUsers` — поиск по юзернейму/имени/фамилии (учитывает `SearchableByUsername`)
- `DeleteAccount` — удаление своего аккаунта (каскад + событие `UserDeleted`)
- `CheckExistUsername`, `CheckExistEmail` (query-handlers)
- `FindByLogin`, `ListByIds`
- `UpdateProfileServer`, `UpdateStorageLimit`

### Privacy/ (вложенная папка)
- `GetPrivacySettings`, `UpdatePrivacySettings` (дефолтная запись создаётся при первом обращении)
- Реально применяется только `SearchableByUsername` (в `SearchUsers`); остальные visibility — хранимые предпочтения (нет графа контактов / трекинга last-seen).

### Draft-flow регистрации
- `AddDraftUser` — добавляет черновик
- `OverrideDraftUser` — переопределяет черновик
- `ConfirmUser` — подтверждение → активный пользователь

### Devices/ (вложенная папка)
- `RegisterDevice`
- `GetDevices` (свои), `GetCurrentDevice`
- `GetUserDevices` (серверный, чужие устройства)
- `RenameDevice`
- `DeleteUserDevice` (серверный), `DeleteDevice` (клиентский, своё устройство)
- `SetFirebaseToken` — push-токен текущего устройства

### Прочее
- `GetUserContacts`

> **Не реализовано** (из ожиданий по proto): `SearchUsersServer`, `Badges/`, `ChatFolders/`, `ExportData/`, `Personalization/`, `Prekeys/`. Из proto также объявлены `GetById` — handler есть в Host через `GetUserQuery`.

> **Пароль не здесь**: смена пароля — в [[modules/backend-identity]] (`SetPassword`). В Users `PasswordHasher` и `UserChangedPasswordEvent` удалены как неиспользуемые.

## События RabbitMQ

Публикуются (по `UserInfoQueueSender.cs`): `UserChangedName`, `UserChangedUsername`, `UserChangedAvatar`, `UserChangedBio` (при `ChangeBio`), `UserDeleted` (при `DeleteAccount`). DTO `UserChangedPassword` остаётся в [[modules/shared-queue]], но из Users больше не публикуется.

> `UserDeleted` обрабатывают консьюмеры в [[modules/backend-identity]] (отзыв сессий + удаление пароля/2FA/сбросов/кодов) и [[modules/backend-files]] (открепление блобов из Uploaders + удаление каталогов/записей/альбомов). Физическая очистка осиротевших S3-блобов — отдельная фоновая задача (как и при ручном удалении).

Слушает: `SessionRevokedEvent`.

## Зависимости

- Использует: `BarkCloud.Proto`, `BarkCloud.GrpcServer`, `BarkCloud.Shared.*`, EF Core, PostgreSQL, RabbitMQ
