# Backend — Users

Parent: [[index]] · See also: [[api/users-api]] · [[modules/shared-queue]]

## Назначение

Сервис пользователей: профили (имя, юзернейм), draft-flow регистрации, устройства, контакты, аватарка, лимиты хранилища, проверки уникальности.

## Расположение

`Backend/BarkCloud.Users/`

## Файлы

### Domain
- `User.cs` — основная сущность пользователя
- `UserDevice.cs` — устройство (поля: `Id`, `UserId`, `OriginalName`, `CustomName`, `AuthorizedAt`, `AppName`, `OperationSystem`, `Location`)
- `UserContact.cs` — контакт

> **Не реализовано в Domain**: Badge, ChatFolder, DevicePrekeyBundle, OneTimePrekey, Privacy, ProfileFieldVisibility, UserPersonalization, UserBadge. Если появятся — обновить эту заметку.

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
- `Services/UsersStorage.cs`
- `Services/DevicesStorage.cs`
- `Migrations/20260518171439_InitialCreate.cs` — единственная миграция на момент актуализации

## Features (реализованные)

### Профиль
- `GetUser` — query
- `SetProfilePicture`, `SetProfilePictureServer`
- `ChangeName`, `ChangeUsername`
- `CheckExistUsername`, `CheckExistEmail` (query-handlers)
- `FindByLogin`, `ListByIds`
- `UpdateProfileServer`, `UpdateStorageLimit`

### Draft-flow регистрации
- `AddDraftUser` — добавляет черновик
- `OverrideDraftUser` — переопределяет черновик
- `ConfirmUser` — подтверждение → активный пользователь

### Devices/ (вложенная папка)
- `RegisterDevice`
- `GetDevices` (свои), `GetCurrentDevice`
- `GetUserDevices` (серверный, чужие устройства)
- `RenameDevice`
- `DeleteUserDevice`

### Прочее
- `GetUserContacts`

> **Не реализовано** (из ожиданий по proto): `ChangeBio`, `SearchUsers`, `SearchUsersServer`, `Badges/`, `ChatFolders/`, `ExportData/`, `Personalization/`, `Prekeys/`, `Privacy/`, `SetFirebaseToken`. Из proto также объявлены `GetById`, но handler пока отсутствует в `Features/`.

## События RabbitMQ

Сейчас в [[modules/shared-queue]] определены `UserChangedAvatar/Bio/Name/Username/Password`. Реально публикуются (по `UserInfoQueueSender.cs`) при `ChangeName`/`ChangeUsername`/`SetProfilePicture` — остальные пока без обработчиков-публикаторов в коде.

Слушает: `SessionRevokedEvent`.

## Зависимости

- Использует: `BarkCloud.Proto`, `BarkCloud.GrpcServer`, `BarkCloud.Shared.*`, EF Core, PostgreSQL, RabbitMQ
