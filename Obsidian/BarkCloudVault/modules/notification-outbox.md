# Outbox уведомлений Identity (F19)

Parent: [[modules/backend-identity]] · See also: [[modules/transactional-outbox]] · [[modules/shared-queue]] · [[modules/backend-notification]] · [[structure/testing]]

## Зачем

Раньше Identity сначала сохранял основное изменение (пароль, сессия, подтверждение аккаунта, отключение 2FA), а затем на критическом пути ходил в Users за контактами, во внешний `ip-api.com` за геолокацией и публиковал письмо в RabbitMQ. Сбой любого шага возвращался клиенту как ошибка всей операции: «смена пароля не удалась», хотя пароль уже новый, а повтор со старым паролем отклонялся. `ConfirmAccount` оставлял подтверждённый аккаунт без refresh и с уже удалённым кодом. Там, где ошибка уже перехватывалась, письмо просто терялось.

Теперь хендлер после основного коммита только вставляет строку в таблицу `PendingNotifications`. Письмо собирает и отправляет фоновый воркер, поэтому итог операции от Users, геолокации и RabbitMQ не зависит.

Выбран собственный outbox, а не MassTransit EF bus outbox ([[modules/transactional-outbox]] у Users): адрес берётся из Users до публикации, и штатный outbox не пережил бы падение Users — письмо потерялось бы ещё до постановки.

## Компоненты (`Backend/BarkCloud.Identity/`)

| Файл | Роль |
|---|---|
| `Domain/PendingNotification.cs` | Строка очереди: `UserId`, `Type`, `Title`, `PayloadJson`, `CreatedAt`, `NextAttemptAt`, `Attempts`, `LockedUntil`, `LockToken`. Индекс по `NextAttemptAt` |
| `Persistence/Migrations/20261002112945_AddPendingNotifications` | Аддитивная миграция (таблица + индекс); `Down` удаляет таблицу |
| `Services/INotificationOutbox`, `NotificationOutbox` | `EnqueueAsync(userId, type, title, payload)` — **не бросает**: сбой записи → warning и метрика `notification_outbox_enqueue_failed`, сущность отсоединяется от контекста. Режим без почты (`Features:EmailEnabled=false`) — строк не пишет |
| `Services/NotificationPayload` | Общий блок полей `ip`, `devicename`, `os`, `appname`, `datetime` из `RequestContext` или явных значений; `datetime` — время события |
| `Services/NotificationOutboxWorker` | `BackgroundService`: захват, доставка, повторы |
| `Services/PasswordChangedNotifier` | Тонкая обёртка: `NotifyAsync(userId)` ставит «Пароль успешно изменен» в outbox |

В `payload` хендлеры не передают `username` (воркер берёт его из Users) и `location` (воркер определяет по `ip`, если значение не задано; `SessionIssuer` передаёт уже полученное, чтобы не делать второй запрос к `ip-api`). Письмо без `ip` (например, смена пароля администратором) геолокацию не получает.

## Воркер

Константы без настроек: опрос 5 с (полная пачка — без паузы), пачка 20, lease 2 мин, срок жизни 24 ч, пауза после неудачи `30 с · 2^(n−1)` не больше 1 ч, дедлайн запроса контактов 10 с. Стартует после `Database.Migrate()`.

1. **Захват** (несколько реплик Identity, без raw SQL): выбрать id готовых строк (`NextAttemptAt <= now`, lease свободен или истёк), условным `ExecuteUpdate` записать `LockToken`, `LockedUntil = now + lease`, `Attempts + 1`, прочитать строки со своим токеном. Условие повторено в UPDATE: строку, взятую другой репликой, получает она одна.
2. **Доставка строки** в отдельном scope: строка старше 24 ч — удаляется (`notification_outbox_expired`); `GetUserContactsAsync` → нет адреса — удаляется (`notification_outbox_dropped`); добавляются `username` и при необходимости `location`; `NotificationQueueSender.SendNotification`; успех — строка удаляется (`notification_outbox_sent`).
3. **Сбой** любого шага: `NextAttemptAt = now + пауза`, lease снимается, `notification_outbox_failed`. Остановка сервиса оставляет lease: строка вернётся в очередь после его истечения.

## Гарантии и ограничения

- Доставка **как минимум однократная**: падение между публикацией и удалением строки даст повтор письма (как в F11).
- Строка ставится **после** основного коммита, отдельным `SaveChanges`: если процесс упадёт именно между ними, письмо потеряется. Окно намного уже прежнего сценария «недоступен Users». Внутри транзакции enqueue не вызывается: проглоченная ошибка вставки испортила бы транзакцию PostgreSQL.
- Письма с кодами (`CreateAccount`, `ResetPassword`, `EnableOtpVerification` для email, email-код входа в `Auth`) остаются синхронными: письмо там — суть операции и ошибка должна дойти до клиента. Они только выигрывают от таймаута геолокации.
- Строки пользователей, удалённых до доставки, повторяются до истечения 24 ч и затем удаляются.
- До доставки или 24 ч в таблице хранятся IP и название устройства; отправленные строки удаляются сразу.

## Какие уведомления идут через outbox

`Auth` (`FailedLogin`; успешный вход — через `SessionIssuer`), `SessionIssuer` (`SuccessfulLogin`; им пользуются вход паролем, вход по ключу и `CreateSessionForUserServer`), `PasswordChangedNotifier` (`SetPassword`, `ConfirmResetPassword`), `DisableOtpVerification`, `ConfirmOtpVerification` (`TwoFactorMethodChanged`), `ConfirmAccount` (`SuccessfulRegistration`), `ForceSetPasswordServer` (`PasswordChangedByAdmin`).

## Геолокация

`LocationClient.GetLocation` ждёт не дольше `LocationClient.RequestTimeout` (2 с, токен отмены в `GetAsync`/`ReadAsStringAsync`); по таймауту возвращает `null` (в письме и названии устройства — «-»), метрики `geolocation_errors` и `geolocation_timeouts`. Таймаут задан внутри класса, а не в `AddHttpClient<LocationClient>`: следующая в `Program.cs` регистрация `AddScoped<LocationClient>()` перекрывает типизированный клиент, и его настройка `HttpClient` не применилась бы. Сигнатуру `GetLocation(string)` менять нельзя — её мокают тесты.

## Диагностика

```sql
SELECT count(*) AS pending,
       now() - min("CreatedAt") AS oldest_age,
       max("Attempts") AS max_attempts
FROM "PendingNotifications";
```

Рост `pending` и возраст при недоступных Users/RabbitMQ ожидаем; после восстановления очередь должна уменьшаться за несколько проходов. Метрики: `notification_outbox_enqueued`, `_enqueue_failed`, `_sent`, `_failed`, `_expired`, `_dropped`.

## Порядок обновления

Миграция применяется при старте Identity (`Database.Migrate()`) до запуска воркера. Откат приложения оставляет таблицу; перед `Down` миграции дождаться опустошения очереди — ожидающие письма будут потеряны вместе с таблицей.

## Проверки

- `Identity.Tests`: `LocationClientTests` (зависший сервер → `null` за ≈2 с), `NotificationOutboxTests`, `NotificationOutboxWorkerTests` (успех; падение Users или публикации — строка остаётся, растёт пауза; доставка после восстановления; будущая строка и чужой lease не захватываются; просроченный lease захватывается; >24 ч и пустой email удаляются; пачка 20), `SessionIssuerTests` (в том числе зависшая геолокация не задерживает вход), `SetPassword` с недоступной таблицей очереди.
- `Identity.IntegrationTests` (PostgreSQL): два воркера на 60 общих строках — каждое письмо доставлено ровно раз; истёкший lease; сценарий «Users недоступен после записи пароля — клиент получил успех, письмо доставлено после восстановления»; миграция `Down → Up` сохраняет остальные данные Identity. См. [[structure/testing]].
