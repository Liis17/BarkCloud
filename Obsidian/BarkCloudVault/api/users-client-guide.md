# Users — Клиентский гайд по API (профиль, аватар, имя/юзернейм/bio, приватность, устройства, аккаунт)

Parent: [[index]] · Proto: [[modules/shared-proto]] · Backend: [[modules/backend-users]] · API-обзор: [[api/users-api]]

> Назначение: самодостаточная инструкция для разработчика **клиента** (Android/iOS/web). Описывает что передать и что вернётся для каждого эндпоинта работы с профилем и аккаунтом: данные пользователя, аватар, имя/фамилия/юзернейм/bio, поиск, настройки приватности, устройства и push-токен, удаление аккаунта. Серверная реализация — в [[modules/backend-users]].

## 0. Общее

- **Транспорт**: gRPC. Контракт: `Shared/BarkCloud.Proto/users_api.proto`, package `barkcloud.users`, C#-namespace `BarkCloud.Proto.Users`. Клиентский сервис — **`UsersApi`**.
- **Авторизация**: `UsersApi` требует пользовательский токен (политика `User`). Токен передаётся в gRPC-метадате заголовком `x-auth-token`. Без валидного токена — `Unauthenticated`.
  - **Исключения (AllowAnonymous)**: `CheckExistUsername`, `CheckExistEmail` — работают без токена (нужны на регистрации).
- **Идентификация пользователя**: сервер берёт `userId` и `deviceId` **из токена**, клиент их не передаёт (кроме явных полей вроде `user_id` в `GetUser`). Эндпоинты смены данных всегда действуют на **текущего** пользователя.
- **Идентификаторы устройств**: строки с GUID (`"6f9619ff-8b86-d011-b42d-00cf4fc964ff"`).
- **Время**: `google.protobuf.Timestamp` (UTC).
- **Ошибки**: доменные ошибки приходят как gRPC `FailedPrecondition` с `ErrorCode` (GUID) в trailing-метадате — см. §8. Сетевые/прочие — стандартные gRPC-статусы.
- **Пустые ответы**: многие методы возвращают пустое сообщение (`*Response { }`) — успех = отсутствие ошибки.

### ⚠️ Что НЕ в этом сервисе
- **Смена пароля, сброс пароля, 2FA, логин, сессии, logout** — это сервис **Identity** (`IdentityApi`): `SetPassword(password, old_password)`, `ResetPassword`/`ConfirmResetPassword`, `Auth`, `GetActiveSessions`/`RemoveActiveSession`, `Logout`. См. [[api/identity-api]].
- **Загрузка байтов аватара** — это сервис **Files** (см. §2 и [[api/files-client-guide]]).

---

## 1. Тип `User` (ключевой объект ответов)

Возвращается в `GetUser`, `SearchUsers` и др.

| Поле | Тип | Что значит для клиента |
|------|-----|------------------------|
| `id` | int64 | ID пользователя |
| `first_name` | string | Имя |
| `last_name` | string | Фамилия |
| `username` | string | Юзернейм (уникальный) |
| `registration_date` | Timestamp | Дата регистрации |
| `profile_picture` | string | Прямая ссылка на аватар (полное изображение); пустая строка — аватара нет |
| `profile_picture_preview` | string | Прямая ссылка на превью аватара; пустая — нет |
| `storage_limit_gb` | int32 | Лимит облачного хранилища, ГБ |
| `bio` | string | Описание «о себе» (до 200 символов); пустая строка — не задано |

> `profile_picture` / `profile_picture_preview` — публичные URL, грузятся в `<img>` напрямую, без доп. запросов.

---

## 2. Профиль: чтение и базовые правки

### GetUser — получить профиль
`UsersApi.GetUser`
- Передать: `GetUserRequest { int64 user_id }`
  - `user_id = 0` (или не задан) → **свой** профиль.
  - `user_id = <чужой id>` → профиль другого пользователя.
- Вернётся: `GetUserResponse { User user }`
- Ошибки: `UserNotFound`.
- Когда: экран профиля (своего и чужого), обновление данных после правок.

### ChangeName — сменить имя/фамилию
`UsersApi.ChangeName`
- Передать: `ChangeNameRequest { string first_name, string last_name }` (сервер тримит пробелы)
- Вернётся: `ChangeNameResponse { }`
- Когда: редактирование профиля. Публикует событие `UserChangedName`.

### ChangeUsername — сменить юзернейм
`UsersApi.ChangeUsername`
- Передать: `ChangeUsernameRequest { string username }`
- Вернётся: `ChangeUsernameResponse { }`
- Ошибки: `UsernameReserved` (имя зарезервировано). Для проверки занятости — `CheckExistUsername` заранее.
- Когда: смена юзернейма. Публикует `UserChangedUsername`.

### ChangeBio — сменить описание
`UsersApi.ChangeBio`
- Передать: `ChangeBioRequest { string bio }`
  - Пустая строка `""` → **очистить** bio.
  - Максимум **200** символов.
- Вернётся: `ChangeBioResponse { }`
- Ошибки: `BioTooLong` (> 200 символов).
- Когда: редактирование «о себе». Публикует `UserChangedBio`.

### CheckExistUsername / CheckExistEmail — проверка занятости (без токена)
`UsersApi.CheckExistUsername` / `UsersApi.CheckExistEmail`
- Передать: `{ string username }` / `{ string email }`
- Вернётся: `CheckExistResponse { bool exist }`
- Когда: валидация на формах регистрации/смены юзернейма до отправки.

---

## 3. Аватар

Аватар ставится в **два шага**: сначала файл грузится в сервис Files как `USER_AVATAR`, затем его `file_id` привязывается в Users.

**Шаг 1 — загрузить картинку в Files** (см. [[api/files-client-guide]] §2, но `file_type = USER_AVATAR`):
- `FilesApi.GetUploadUrl { file_type: USER_AVATAR }` → `{ url, file_id }`
- `POST {url}` (multipart, поле `file`) → `{ "fileId": "<guid>" }` (используйте `fileId` из ответа).

**Шаг 2 — установить аватар** (`UsersApi.SetProfilePicture`):
- Передать: `SetProfilePictureRequest { string file_id }`
  - `file_id` = GUID загруженного файла (типа `USER_AVATAR`).
  - Пустая строка `""` → **удалить** аватар.
- Вернётся: `SetProfilePictureResponse { }`
- Ошибки: `ProfilePictureHasNotValidType` — файл не типа `USER_AVATAR`.
- Эффект: в `User.profile_picture` / `profile_picture_preview` появятся готовые URL. Публикует `UserChangedAvatar`.
- Когда: установка/смена/удаление аватара.

---

## 4. Поиск пользователей

`UsersApi.SearchUsers`
- Передать: `SearchUsersRequest { string query, int32 limit }`
  - `query` — подстрока юзернейма/имени/фамилии, **минимум 2 символа** (иначе вернётся пустой список без ошибки).
  - `limit` — 1..50, по умолчанию 20 (0 → 20, > 50 → 50).
- Вернётся: `SearchUsersResponse { repeated User users }`
- Поведение: регистронезависимо; **исключает самого себя** и пользователей с выключенным `searchable_by_username` (§5); только активные (не draft).
- Когда: поиск контакта по нику/имени.

---

## 5. Настройки приватности

Перечисление `PrivacyVisibility`:
```
PRIVACY_VISIBILITY_EVERYONE = 0; // всем
PRIVACY_VISIBILITY_CONTACTS = 1; // только контактам
PRIVACY_VISIBILITY_NOBODY   = 2; // никому
```

Объект `PrivacySettings`:
| Поле | Тип | Смысл |
|------|-----|-------|
| `profile_visibility` | PrivacyVisibility | Кто видит профиль (аватар/имя/bio) |
| `email_visibility` | PrivacyVisibility | Кто видит email |
| `last_seen_visibility` | PrivacyVisibility | Кто видит время последнего захода |
| `searchable_by_username` | bool | Находится ли пользователь через `SearchUsers` |

### GetPrivacySettings
- Передать: `GetPrivacySettingsRequest { }`
- Вернётся: `GetPrivacySettingsResponse { PrivacySettings settings }`
- При первом обращении сервер создаёт запись с дефолтами: profile=EVERYONE, email=NOBODY, last_seen=EVERYONE, searchable=true.

### UpdatePrivacySettings
- Передать: `UpdatePrivacySettingsRequest { PrivacySettings settings }` — **объект целиком** (передавайте все поля, а не дельту).
- Вернётся: `UpdatePrivacySettingsResponse { PrivacySettings settings }` — актуальное состояние.
- Когда: экран настроек приватности.

> ⚠️ Сейчас на сервере реально **применяется только `searchable_by_username`** (фильтр в `SearchUsers`). `profile_visibility` / `email_visibility` / `last_seen_visibility` **хранятся** как предпочтения, но ещё не enforce-ятся (нет графа контактов и трекинга last-seen). Клиент может их отображать/редактировать и опираться на них в UI, но не считать строгой защитой.

---

## 6. Устройства

Тип `Device`:
| Поле | Тип | Описание |
|------|-----|----------|
| `device_id` | string (GUID) | ID устройства |
| `user_id` | int64 | Владелец |
| `original_name` | string | Имя устройства при авторизации |
| `custom_name` | string | Пользовательское имя (может быть пустым) |
| `authorized_at` | Timestamp | Время авторизации |
| `app_name` | string | Имя приложения |
| `operation_system` | string | ОС |
| `location` | string | Страна/город (по IP) |

> `firebase_token` в `Device` **не отдаётся** (служебное поле, только запись через `SetFirebaseToken`).

| RPC | Передать | Вернётся | Когда |
|-----|----------|----------|-------|
| `GetDevices` | `GetDevicesRequest { }` | `GetDevicesResponse { repeated Device devices }` | Список своих устройств (от новых к старым) |
| `GetCurrentDevice` | `GetCurrentDeviceRequest { }` | `GetCurrentDeviceResponse { Device device }` | Текущее устройство (определяется по токену; `device` пустой, если не определено) |
| `RenameDevice` | `RenameDeviceRequest { string device_id, string custom_name }` | `RenameDeviceResponse { }` | Переименовать устройство |
| `DeleteDevice` | `DeleteDeviceRequest { string device_id }` | `DeleteDeviceResponse { }` | Удалить/отвязать **своё** устройство (идемпотентно: нет устройства — без ошибки) |
| `SetFirebaseToken` | `SetFirebaseTokenRequest { string firebase_token }` | `SetFirebaseTokenResponse { }` | Сохранить push-токен на **текущем** устройстве; `""` — сбросить |

> **DeleteDevice vs выход из сессии**: `DeleteDevice` убирает запись об устройстве в Users. Отзыв активной сессии/токена — отдельно через `IdentityApi.RemoveActiveSession(device_id)` ([[api/identity-api]]). Для полноценного «выйти на устройстве» клиент обычно вызывает оба (Identity отзывает сессию и сам инициирует удаление устройства в Users).
> **SetFirebaseToken**: токен привязывается к устройству из токена (`deviceId` в claim). Если устройство не определено — `FailedPrecondition`/ошибка. Вызывать после логина и при обновлении FCM-токена.

---

## 7. Удаление аккаунта

`UsersApi.DeleteAccount`
- Передать: `DeleteAccountRequest { }`
- Вернётся: `DeleteAccountResponse { }`
- Эффект: удаляет профиль пользователя и каскадно — контакт, устройства, настройки приватности. Публикует событие `UserDeleted`.
- ⚠️ **Необратимо**. Клиент должен после успеха: очистить локальные токены/кеши и увести на экран логина.
- По событию `UserDeleted` сервер чистит сопутствующие данные **асинхронно**: Identity отзывает все сессии (refresh + access) и удаляет пароль/2FA/сбросы/коды; Files открепляет блобы пользователя (освобождает квоту) и удаляет каталоги/записи/альбомы. Это происходит не мгновенно — после `DeleteAccount` access-токен может ещё короткое время приниматься, пока не разойдётся событие отзыва.
- Когда: «Удалить аккаунт» в настройках. Рекомендуется доп. подтверждение в UI (повторный ввод/диалог), т.к. серверного re-auth здесь нет.

---

## 8. Ошибки (доменные)

Приходят как gRPC `FailedPrecondition`, в trailing-метадате — `ErrorCode` (GUID). Сопоставление по смыслу:
- `UserNotFound` — пользователь не найден.
- `ProfilePictureHasNotValidType` — `SetProfilePicture` с файлом не типа `USER_AVATAR`.
- `BioTooLong` — bio превышает 200 символов.
- `UsernameReserved` — юзернейм зарезервирован системой.
- `UserIsDraft` — операция над неподтверждённым (draft) пользователем.

Клиенту достаточно показать локализованное сообщение и/или обработать ключевые коды (`UsernameReserved`, `BioTooLong`, `ProfilePictureHasNotValidType`).

---

## 9. Шпаргалка «экран → вызовы»

- **Свой профиль**: `GetUser(user_id=0)`.
- **Чужой профиль**: `GetUser(user_id=<id>)`.
- **Редактировать профиль**: `ChangeName`, `ChangeUsername` (+ заранее `CheckExistUsername`), `ChangeBio`.
- **Аватар**: Files `GetUploadUrl(USER_AVATAR)` → `POST {url}` → `SetProfilePicture(file_id)`; удалить — `SetProfilePicture("")`.
- **Поиск людей**: `SearchUsers(query, limit)`.
- **Настройки приватности**: `GetPrivacySettings` → правки → `UpdatePrivacySettings(settings)`.
- **Список устройств / сессий**: `GetDevices` (+ `GetActiveSessions` из Identity для деталей сессии).
- **Выйти на устройстве**: `IdentityApi.RemoveActiveSession(device_id)` (+ при необходимости `DeleteDevice`).
- **Push**: после логина и при ротации FCM — `SetFirebaseToken(token)`.
- **Сменить пароль**: `IdentityApi.SetPassword(password, old_password)` — **в Identity, не тут**.
- **Удалить аккаунт**: `DeleteAccount()` → очистить токены локально.
