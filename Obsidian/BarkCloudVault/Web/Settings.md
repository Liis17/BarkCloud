# Web — настройки

Parent: [[Web/WebApp]] · See also: [[Backend/Configuration]] · [[Backend/Identity]] · [[Backend/Users]] · [[Web/SystemUpdates]] · [[Web/S3Migration]]

## Назначение

Страница `/settings` объединяет профиль, приватность, безопасность учётной записи, сессии, конфигурацию сервера и состояние хранилища. Браузер вызывает Web HTTP API; Web передаёт операции в Users, Identity и Configuration. Действия сервера требуют пользовательскую сессию и дополнительную разблокировку AdminGate.

## Источники

Раздел `/settings#tags` управляет личными поисковыми тегами через Files (`Backend/BarkCloud.Web/ClientApp/src/pages/TagsTab.tsx`): список с цветами и поиском, создание, изменение названия/цвета и удаление с подтверждением. AdminGate не требуется. Переименование и удаление распространяются на назначения тега всем файлам владельца.

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Web/SettingsEndpoints.cs` | `MapSettingsEndpoints` | HTTP API страницы настроек |
| `Backend/BarkCloud.Web/Rendering/PageDataBuilder.cs` | `PageDataBuilder` | Данные контекста, профиля, хранилища и системы |
| `Backend/BarkCloud.Web/Infrastructure/ConfigurationManagementGateway.cs` | `ConfigurationManagementGateway` | Доступ Web к настройкам Configuration |
| `Backend/BarkCloud.Web/ClientApp/src/pages/SettingsPage.tsx` | `SettingsPage` | Навигация и загрузка разделов |

## Публичные контракты

| Метод и путь | Поведение |
|---|---|
| `GET /api/settings/full` | Полный набор данных страницы |
| `GET /api/settings/tags`, `POST /api/settings/tags/save`, `POST /api/settings/tags/delete` | Личный каталог Files: `{items:[{id,name,color}]}`; сохранение `{id?,name,color}` (пустой id создаёт), удаление `{id}`. Цвет — `#RRGGBB`, имя — до 50 символов; нормализованные дубликаты запрещены |
| `GET /api/settings/context`, `GET /api/settings/profile`, `GET /api/settings/storage`, `GET /api/settings/system` | Раздельные данные для контекста доступа и соответствующих разделов |
| `POST /api/settings/profile/name`, `POST /api/settings/profile/bio`, `POST /api/settings/profile/username`; `GET /api/settings/profile/username-available?u=` | Изменение имени, биографии и username; проверка доступности username |
| `GET/POST /api/settings/privacy` | Чтение и изменение видимости профиля, email, last seen и поиска по username |
| `/api/settings/security/*`, `GET /api/settings/sessions`, `POST /api/settings/sessions/revoke*` | Пароль, 2FA, WebAuthn, список сессий и отзыв сессий |
| `POST /api/settings/system/registration` | Включить или отключить регистрацию; требуется AdminGate |
| `/api/settings/server/*` | Чтение и изменение серверной конфигурации, версий настроек, storage-профилей и зарезервированных имён |
| `POST /api/settings/server/storage/groups/save` | Создать или изменить группу: `{id?, name, quotaValue, quotaUnit, buckets[{serviceUrl, bucketName}]}` |
| `POST /api/settings/server/storage/groups/delete` | Удалить группу: `{id}`; её бакеты остаются без группы |
| `POST /api/settings/account/delete`, `POST /api/settings/avatar`, `POST /api/settings/avatar/remove` | Удаление аккаунта и операции с аватаром |

API миграции S3 принадлежит [[Web/S3Migration]]. API обслуживания контейнеров описан в [[Web/SystemUpdates]].

## Зависимости и взаимодействия

- Профиль, приватность, устройства, удаление аккаунта и аватар используют Users; пароль, 2FA, WebAuthn и сессии используют Identity; конфигурационные значения, S3-профили и группы квот — Configuration.
- `/api/settings/server/*` требует обычную сессию и открытую AdminGate-сессию. Сама AdminGate управляет отдельной cookie и не заменяет пользовательскую аутентификацию.
- Данные для страницы собирает `PageDataBuilder`; файловая статистика и сведения о сервере доступны отдельными запросами.
- `GET /api/settings/server` возвращает `storageGroups` с квотой в байтах и членами, а также `storageBuckets` — уникальные нормализованные endpoint/bucket с ролями и статусами. Credentials не возвращаются. В «Группах S3» квота задаётся в ГБ, ТБ или ПБ; новые бакеты выбираются из доступных профилей, а сохранение и удаление группы требуют перезапуска Files.

## Ограничения и важные детали

Административные маршруты не становятся публичными после разблокировки: каждый запрос по-прежнему проходит проверку обычной пользовательской сессии.

`ConfigurationManagementGateway` не возвращает браузеру значения чувствительных параметров или S3-ключи: для профилей доступны только признаки `HasAccessKey` и `HasSecretKey`. Квота хранится у группы, а не у профиля.
