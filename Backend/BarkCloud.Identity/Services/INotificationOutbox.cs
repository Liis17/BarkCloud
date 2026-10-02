using BarkCloud.Shared.Queue.Notifications;

namespace BarkCloud.Identity.Services;

/// <summary>
/// Постановка письма в очередь доставки (outbox Identity). Вызывается после основного изменения: итог операции
/// не должен зависеть от Users, геолокации и RabbitMQ — их обращает в письмо фоновый <see cref="NotificationOutboxWorker"/>.
/// </summary>
public interface INotificationOutbox
{
    /// <summary>
    /// Сохраняет письмо для доставки. <b>Не бросает исключений</b>: сбой записи логируется и учитывается метрикой.
    /// В <paramref name="payload"/> не нужны <c>username</c> (воркер возьмёт из Users) и <c>location</c>
    /// (воркер определит по <c>ip</c>, если не задана).
    /// </summary>
    Task EnqueueAsync(long userId, NotificationType type, string title, Dictionary<string, string> payload);
}
