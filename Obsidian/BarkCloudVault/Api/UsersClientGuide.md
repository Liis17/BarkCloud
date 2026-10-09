# Users API — краткий гайд клиенту

Parent: [[Backend/Users]] · See also: [[Shared/Proto]], [[Api/FilesClientGuide]], [[Backend/Identity]]

## Источники

- `Shared/BarkCloud.Proto/users_api.proto` — формы запросов, ответов и сообщений.
- `Backend/BarkCloud.Users/Host/UsersApiService.cs` — обработка клиентских RPC.
- `Backend/BarkCloud.Users/Features/SearchUsers/SearchUsersQueryHandler.cs`, `Backend/BarkCloud.Users/Domain/UserPrivacy.cs` — правила поиска и значения privacy.

## Подключение

Контракт: `Shared/BarkCloud.Proto/users_api.proto`, service `UsersApi`. Для обычных вызовов передавай user JWT в gRPC metadata `x-auth-token`. `CheckExistUsername` и `CheckExistEmail` доступны без токена. Методы с пустым `*Response` завершаются успешно при отсутствии gRPC-ошибки.

## Основные вызовы

| RPC | Что передать | Практическое поведение |
|---|---|---|
| `GetUser` | `user_id` | `0` — текущий пользователь; ненулевое значение — указанный ID |
| `CheckExistUsername` | `username` | Предварительная проверка; reserved username возвращает `exist=true`, черновик считается свободным |
| `CheckExistEmail` | `email` | Предварительная проверка; черновик считается свободным |
| `ChangeName` | `first_name`, `last_name` | Смена имени и фамилии |
| `ChangeUsername` | `username` | Проверку `CheckExistUsername` используй как подсказку; окончательный результат решает запись |
| `ChangeBio` | `bio` | До 200 символов; пустая строка очищает bio |
| `SearchUsers` | `query`, `limit` | От 2 символов; исключает себя, drafts и скрытые из поиска профили. `limit<=0` даёт 20 результатов, максимум — 50 |
| `GetPrivacySettings` | пустой запрос | Возвращает текущие настройки, создаёт запись при первом чтении |
| `UpdatePrivacySettings` | полный объект `settings` | Заменяет настройки целиком |
| `GetDevices`, `GetCurrentDevice` | пустой запрос | Список устройств или устройство из контекста текущего JWT |
| `RenameDevice` | `device_id`, `custom_name` | Переименовывает устройство |
| `DeleteDevice` | `device_id` | Удаляет запись устройства; для управления сессией используй Identity |
| `SetFirebaseToken` | `firebase_token` | Пустая строка сбрасывает push token текущего устройства |
| `DeleteAccount` | пустой запрос | Удаляет аккаунт; после успеха очисти локальные токены и кэш |

## Профиль и устройство

Ответ `User` содержит `id`, `first_name`, `last_name`, `username`, `registration_date`, `profile_picture`, `profile_picture_preview`, `storage_limit_gb` и `bio`. Пустые строки для аватара и bio означают, что значение не задано.

`Device` содержит `device_id`, `user_id`, `original_name`, `custom_name`, `authorized_at`, `app_name`, `operation_system` и `location`. Push token в ответ не включается.

Для смены аватара сначала загрузи файл в Files с типом `USER_AVATAR`, затем передай возвращённый `file_id` в `SetProfilePicture`. Пустой `file_id` очищает аватар. Подробный upload flow: [[Api/FilesClientGuide]].

Privacy request передаёт весь объект: `profile_visibility`, `email_visibility`, `last_seen_visibility`, `searchable_by_username`. Начальные значения: `EVERYONE`, `NOBODY`, `EVERYONE`, `true`. Сервер применяет `searchable_by_username` в `SearchUsers`; остальные visibility-поля сохраняются как настройки.

## Ошибки и смежные операции

Известные доменные ошибки приходят как gRPC `FailedPrecondition` с trailer `x-error-code`; при подключённом `ExceptionClientInterceptor` известный код восстанавливается в типизированное исключение. Для UI важны `UsernameReservedException`, `UsernameExistException`, `BioTooLongException`, `ProfilePictureHasNotValidType` и `UserNotFoundException`.

Отзыв сессии, вход, смена/сброс пароля и logout принадлежат `IdentityApi`; удаление записи устройства само по себе не отзывает токен. Удаление аккаунта публикует `UserDeleted`, последующая очистка Identity и Files происходит асинхронно.
