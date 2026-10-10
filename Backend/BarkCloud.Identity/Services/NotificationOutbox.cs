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

    public async Task<bool> EnqueueAsync(long userId, NotificationType type, string title, Dictionary<string, string> payload,
        CancellationToken cancellationToken = default)
    {
        // Режим без почты: строки не копим (как NotificationQueueSender, который глушит публикацию).
        if (!_emailEnabled)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var notification = new PendingNotification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Title = title,
            PayloadJson = string.Empty,
            CreatedAt = now,
            NextAttemptAt = now
        };

        try
        {
            notification.PayloadJson = JsonSerializer.Serialize(payload);
            context.PendingNotifications.Add(notification);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            metrics.Increment("notification_outbox_enqueue_failed");
            logger.LogWarning(ex,
                "Не удалось поставить уведомление {Type} для пользователя {UserId} в очередь доставки", type, userId);
            context.Entry(notification).State = EntityState.Detached;
            throw;
        }
    }
}
