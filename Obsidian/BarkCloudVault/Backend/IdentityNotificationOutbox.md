# Backend — Identity notification outbox

Parent: [[Backend/Identity]] · See also: [[Backend/Notification]], [[Shared/SharedLibraries]]

## Назначение

Локальная очередь Identity для писем, которые не должны зависеть от доступности Users, геолокации или RabbitMQ в момент основной операции.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Identity/Domain/PendingNotification.cs` | `PendingNotification` | Строка очереди в Identity DB |
| `Backend/BarkCloud.Identity/Services/NotificationOutbox.cs` | `NotificationOutbox` | Запись события в shared DB-транзакцию владельца |
| `Backend/BarkCloud.Identity/Services/NotificationOutboxWorker.cs` | `NotificationOutboxWorker` | Получение адреса и доставка |
| `Backend/BarkCloud.Identity/Services/NotificationPayload.cs` | `NotificationPayload` | Поля устройства для шаблона |
| `Backend/BarkCloud.Identity/Infrastructure/NotificationQueueSender.cs` | `NotificationQueueSender` | Публикация `EmailNotification` |
| `Backend/BarkCloud.Identity/Persistence/Contexts/IdentityContext.cs` | `PendingNotifications` | Таблица очереди |

## Публичные контракты и поток

`INotificationOutbox.EnqueueAsync(userId, type, title, payload, cancellationToken)` создаёт строку с ID, типом письма, JSON payload и временем следующей попытки через тот же scoped `IdentityContext`, что и владелец операции. Метод сохраняет строку с переданным токеном, но внешнюю транзакцию не коммитит; результат вызова становится durable только после коммита владельца. Он возвращает `true` после сохранения и `false`, если `Features:EmailEnabled=false` и строка не создавалась. Ошибки сериализации, записи и отмены логируются/учитываются метрикой, строка отсоединяется от tracker, затем ошибка передаётся владельцу.

Владельцы фиксируют бизнес-изменение и событие одним локальным коммитом. Поэтому ошибка вставки или подтверждённый отказ коммита откатывает их обоих. Успешные метрики постановки в очередь и логи операции записываются владельцем после коммита. Единственный best-effort путь для отказного login-письма находится в `AuthCommandHandler`: ошибка уведомления не заменяет `InvalidLoginOrPasswordException`.

Воркер дополняет payload именем и email из Users; если передан IP и нет `location`, получает локацию через `LocationClient`. Затем публикует `EmailNotification` в RabbitMQ для [[Backend/Notification]].

## Ограничения и важные детали

Воркер опрашивает очередь раз в 5 секунд и обрабатывает до 20 записей за пачку. Для координации реплик используются `LockToken` и двухминутный lease. Сбой доставки снимает lease и назначает повтор: задержка начинается с 30 секунд, удваивается до часа. Запись старше 24 часов удаляется; повторные попытки не продлевают этот TTL.

Доставка допускает повторы: если RabbitMQ принял письмо, но Identity не успела удалить строку, воркер может опубликовать её снова. Тесты подтверждают конкурирующий lease и сохранение строки при ошибках в пределах существующего TTL, но не обещают exactly-once доставку. Пустой email приводит к удалению строки без публикации.
