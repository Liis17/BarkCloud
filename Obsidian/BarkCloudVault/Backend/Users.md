# Backend — Users

Parent: [[Index]] · See also: [[Api/UsersClientGuide]], [[Backend/AccountDeletionOutbox]], [[Shared/SharedLibraries]]

## Назначение

Хранит профиль пользователя, контакт, устройства, настройки приватности и лимит хранилища. Обслуживает клиентский Users API и межсервисные операции регистрации и синхронизации профилей.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Users/Host/UsersApiService.cs` | `UsersApiService` | Клиентские RPC |
| `Backend/BarkCloud.Users/Host/UsersServerApiService.cs` | `UsersServerApiService` | Служебные RPC |
| `Backend/BarkCloud.Users/Domain/User.cs` | `User` | Профиль и состояние draft |
| `Backend/BarkCloud.Users/Domain/UserDevice.cs` | `UserDevice` | Устройство и push token |
| `Backend/BarkCloud.Users/Domain/UserPrivacy.cs` | `UserPrivacy` | Настройки видимости/поиска |
| `Backend/BarkCloud.Users/Persistence/Contexts/UsersContext.cs` | `UsersContext` | Профильные таблицы и outbox-модели |
| `Backend/BarkCloud.Users/Persistence/Services/UsersStorage.cs` | `UsersStorage` | Профили, поиск и уникальность |
| `Backend/BarkCloud.Users/Persistence/Services/DevicesStorage.cs` | `DevicesStorage` | Регистрация устройств |
| `Backend/BarkCloud.Users/Infrastructure/UserInfoQueueSender.cs` | `UserInfoQueueSender` | События профиля и удаления |
| `Shared/BarkCloud.Proto/users_api.proto` | `UsersApi`, `UsersServerApi` | gRPC-контракт |

## Публичные контракты

`UsersApi` использует policy `User` (принимает user или service JWT); `CheckExistUsername` и `CheckExistEmail` разрешены без токена. Клиентам следует передавать user JWT. Клиентский гайд по входным данным и ответам — [[Api/UsersClientGuide]].

| Контракт | Поведение |
|---|---|
| `GetUser`, `SetProfilePicture`, `ChangeName`, `ChangeUsername`, `ChangeBio`, `SearchUsers` | Чтение/изменение профиля и поиск |
| `GetPrivacySettings`, `UpdatePrivacySettings` | Чтение и полная замена privacy settings |
| `GetDevices`, `GetCurrentDevice`, `RenameDevice`, `DeleteDevice`, `SetFirebaseToken` | Устройства текущего пользователя |
| `DeleteAccount` | Удаляет профиль и публикует `UserDeleted` через outbox |
| `UsersServerApi` | Служебные lookup, draft-flow, подтверждение пользователя, устройства, лимит хранилища и администрирование профиля/аватара |

`UsersServerApi` защищён service JWT. `GetById` реализован в `UsersServerApiService` через `GetUserQuery`; API также включает `FindByLogin`, `ListByIds`, `GetUserContacts`, `AddDraftUser`, `OverrideDraftUser`, `ConfirmUser` и служебные операции устройств.

## Данные и взаимодействия

- `UsersContext` хранит `Users`, `UserContacts`, `UserDevices`, `UserPrivacies` и таблицы MassTransit outbox. Удаление пользователя каскадно удаляет контакт, устройства и privacy row.
- Регистрационный draft — та же запись профиля с `IsDraft=true`; `ConfirmUser` снимает этот флаг. Identity вызывает Users для draft-flow и регистрации устройства.
- Уникальные индексы обеспечивают username без учёта регистра для всех строк, включая drafts, и непустой email без учёта регистра. Конфликт записи преобразуется в `UsernameExistException` или `EmailExistException`.
- `SearchUsers` ищет по username/first/last name без учёта регистра, исключает текущего пользователя и drafts, а также пользователей с `SearchableByUsername=false`. Результаты ограничиваются 50; запрос короче 2 символов даёт пустой список.
- Зарезервированные имена читаются из `ReservedNames:Usernames` при создании `ReservedUsernamesService`; обновление списка требует обновить снимок конфигурации Users.
- После изменения имени, username, bio или аватара публикуются `UserChanged*`; удаление публикует `UserDeleted`. Формы сообщений определены в [[Shared/SharedLibraries]].

## Ограничения и важные детали

`ProfileVisibility`, `EmailVisibility` и `LastSeenVisibility` сохраняются как предпочтения, но обработчики чтения профиля их не применяют. В `SearchUsers` применяется только `SearchableByUsername`. `storage_limit_gb=0` означает использовать полный доступный объём хранилища.

Детали транзакции удаления аккаунта и доставки события — в [[Backend/AccountDeletionOutbox]].
