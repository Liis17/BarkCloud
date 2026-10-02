using System.Text.Json;

using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Shared.Queue.Notifications;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Services;

public class NotificationOutbox(
    IdentityContext context,
    IConfiguration configuration,
    MetricsCollector metrics,
    ILogger<NotificationOutbox> logger) : INotificationOutbox
{
    private readonly bool _emailEnabled = configuration.EmailEnabled();

    public async Task EnqueueAsync(long userId, NotificationType type, string title, Dictionary<string, string> payload)
    {
        // Режим без почты: строки не копим (как NotificationQueueSender, который глушит публикацию).
        if (!_emailEnabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var notification = new PendingNotification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Title = title,
            PayloadJson = JsonSerializer.Serialize(payload),
            CreatedAt = now,
            NextAttemptAt = now
        };

        try
        {
            context.PendingNotifications.Add(notification);

            // Основное изменение уже зафиксировано: отмена запроса клиентом не должна терять письмо.
            await context.SaveChangesAsync(CancellationToken.None);

            metrics.Increment("notification_outbox_enqueued");
        }
        catch (Exception ex)
        {
            metrics.Increment("notification_outbox_enqueue_failed");
            logger.LogWarning(ex,
                "Не удалось поставить уведомление {Type} для пользователя {UserId} в очередь доставки", type, userId);
        }
        finally
        {
            // Неудавшаяся вставка не должна повторяться следующим SaveChanges этого же scope.
            context.Entry(notification).State = EntityState.Detached;
        }
    }
}
