using BarkCloud.GrpcServer;
using BarkCloud.Shared.Queue.Notifications;

using MassTransit;

using Microsoft.Extensions.Configuration;

namespace BarkCloud.Identity.Infrastructure;

public class NotificationQueueSender
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly bool _emailEnabled;

    public NotificationQueueSender(IPublishEndpoint publishEndpoint, IConfiguration configuration)
    {
        _publishEndpoint = publishEndpoint;
        _emailEnabled = configuration.EmailEnabled();
    }

    public virtual async Task SendNotification(Notification notification)
    {
        // Режим без почты: не публикуем задачи в очередь Notification, чтобы они не копились
        // (сервис Notification может быть остановлен). Глушит все точки публикации разом.
        if (!_emailEnabled)
        {
            return;
        }

        if (notification is EmailNotification emailNotification)
        {
            await _publishEndpoint.Publish(emailNotification);
        }
    }
}
