using BarkCloud.Shared.Queue.Notifications;

namespace BarkCloud.Identity.Domain;

/// <summary>
/// Письмо, поставленное в очередь на доставку (outbox). Хендлер только вставляет строку после основного изменения;
/// адрес, имя пользователя и геолокацию подбирает фоновый воркер, поэтому сбой Users/RabbitMQ не влияет на ответ клиенту.
/// Успешно отправленная строка удаляется.
/// </summary>
public class PendingNotification
{
    public Guid Id { get; set; }

    public long UserId { get; set; }

    public NotificationType Type { get; set; }

    public string Title { get; set; } = null!;

    /// <summary>JSON-словарь полей письма. Без <c>username</c> (и, если есть <c>ip</c>, без <c>location</c>) — их добавляет воркер.</summary>
    public string PayloadJson { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime NextAttemptAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>До этого момента строку обрабатывает воркер, захвативший <see cref="LockToken"/>.</summary>
    public DateTime? LockedUntil { get; set; }

    public Guid? LockToken { get; set; }
}
