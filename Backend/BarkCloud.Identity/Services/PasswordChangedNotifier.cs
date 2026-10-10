using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Shared.Queue.Notifications;

namespace BarkCloud.Identity.Services;

// Письмо «Пароль успешно изменен» — общий хвост смены пароля (SetPassword) и сброса (ConfirmResetPassword).
// Ставит письмо в outbox той же локальной транзакции, что и смена пароля.
public class PasswordChangedNotifier(INotificationOutbox notificationOutbox, RequestContext requestContext)
{
    public virtual Task<bool> NotifyAsync(long userId, CancellationToken cancellationToken = default)
        => notificationOutbox.EnqueueAsync(
            userId,
            NotificationType.PasswordChanged,
            "Пароль успешно изменен",
            NotificationPayload.Device(requestContext), cancellationToken);
}
