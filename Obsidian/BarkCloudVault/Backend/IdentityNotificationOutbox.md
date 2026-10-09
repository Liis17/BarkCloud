# Backend — Identity notification outbox

Parent: [[Backend/Identity]] · See also: [[Backend/Notification]], [[Shared/SharedLibraries]]

## Назначение

Локальная очередь Identity для писем, которые не должны зависеть от доступности Users, геолокации или RabbitMQ в момент основной операции.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Identity/Domain/PendingNotification.cs` | `PendingNotification` | Строка очереди в Identity DB |
| `Backend/BarkCloud.Identity/Services/NotificationOutbox.cs` | `NotificationOutbox` | Best-effort запись |
| `Backend/BarkCloud.Identity/Services/NotificationOutboxWorker.cs` | `NotificationOutboxWorker` | Получение адреса и доставка |
| `Backend/BarkCloud.Identity/Services/NotificationPayload.cs` | `NotificationPayload` | Поля устройства для шаблона |
| `Backend/BarkCloud.Identity/Infrastructure/NotificationQueueSender.cs` | `NotificationQueueSender` | Публикация `EmailNotification` |
| `Backend/BarkCloud.Identity/Persistence/Contexts/IdentityContext.cs` | `PendingNotifications` | Таблица очереди |

## Публичные контракты и поток

`INotificationOutbox.EnqueueAsync(userId, type, title, payload)` создаёт запись с ID, типом письма, JSON payload и временем следующей попытки. Воркер дополняет payload именем и email из Users; если передан IP и нет `location`, получает локацию через `LocationClient`. Затем публикует `EmailNotification` в RabbitMQ для [[Backend/Notification]].

При `Features:EmailEnabled=false` очередь не пополняется. Ошибка записи outbox логируется, но не передаётся вызывающей операции; письмо не является частью результата входа или смены пароля.

## Ограничения и важные детали

Воркер опрашивает очередь раз в 5 секунд и обрабатывает до 20 записей за пачку. Для координации реплик используются `LockToken` и двухминутный lease. Сбой доставки снимает lease и назначает повтор: задержка начинается с 30 секунд, удваивается до часа. Запись старше 24 часов удаляется.

Доставка как минимум однократная: если RabbitMQ принял письмо, но Identity не успела удалить запись, письмо может быть отправлено повторно. Пустой email приводит к удалению записи без публикации.
