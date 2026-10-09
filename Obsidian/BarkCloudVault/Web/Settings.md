# Web — настройки

Parent: [[Web/WebApp]] · See also: [[Backend/Configuration]] · [[Backend/Identity]] · [[Backend/Users]] · [[Web/SystemUpdates]] · [[Web/S3Migration]]

## Назначение

Страница `/settings` объединяет профиль, приватность, безопасность учётной записи, сессии, конфигурацию сервера и состояние хранилища. Браузер вызывает Web HTTP API; Web передаёт операции в Users, Identity и Configuration. Действия сервера требуют пользовательскую сессию и дополнительную разблокировку AdminGate.

## Источники

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
| `GET /api/settings/context`, `GET /api/settings/profile`, `GET /api/settings/storage`, `GET /api/settings/system` | Раздельные данные для контекста доступа и соответствующих разделов |
| `POST /api/settings/profile/name`, `POST /api/settings/profile/bio`, `POST /api/settings/profile/username`; `GET /api/settings/profile/username-available?u=` | Изменение имени, биографии и username; проверка доступности username |
| `GET/POST /api/settings/privacy` | Чтение и изменение видимости профиля, email, last seen и поиска по username |
| `/api/settings/security/*`, `GET /api/settings/sessions`, `POST /api/settings/sessions/revoke*` | Пароль, 2FA, WebAuthn, список сессий и отзыв сессий |
| `POST /api/settings/system/registration` | Включить или отключить регистрацию; требуется AdminGate |
| `/api/settings/server/*` | Чтение и изменение серверной конфигурации, версий настроек, storage-профилей и зарезервированных имён |
| `POST /api/settings/account/delete`, `POST /api/settings/avatar`, `POST /api/settings/avatar/remove` | Удаление аккаунта и операции с аватаром |

API миграции S3 принадлежит [[Web/S3Migration]]. API обслуживания контейнеров описан в [[Web/SystemUpdates]].

## Зависимости и взаимодействия

- Профиль, приватность, устройства, удаление аккаунта и аватар используют Users; пароль, 2FA, WebAuthn и сессии используют Identity; конфигурационные значения и S3-профили — Configuration.
- `/api/settings/server/*` требует обычную сессию и открытую AdminGate-сессию. Сама AdminGate управляет отдельной cookie и не заменяет пользовательскую аутентификацию.
- Данные для страницы собирает `PageDataBuilder`; файловая статистика и сведения о сервере доступны отдельными запросами.

## Ограничения и важные детали

Административные маршруты не становятся публичными после разблокировки: каждый запрос по-прежнему проходит проверку обычной пользовательской сессии.

`ConfigurationManagementGateway` не возвращает браузеру значения чувствительных параметров или S3-ключи: для профилей доступны только признаки `HasAccessKey` и `HasSecretKey`.
