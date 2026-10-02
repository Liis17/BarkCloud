using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Shared.Queue.Notifications;

namespace BarkCloud.Identity.Services;

// Письмо «Пароль успешно изменен» — общий хвост смены пароля (SetPassword) и сброса (ConfirmResetPassword).
// Только ставит письмо в outbox и не бросает: пароль к этому моменту уже изменён.
public class PasswordChangedNotifier(INotificationOutbox notificationOutbox, RequestContext requestContext)
{
    public virtual Task NotifyAsync(long userId)
        => notificationOutbox.EnqueueAsync(
            userId,
            NotificationType.PasswordChanged,
            "Пароль успешно изменен",
            NotificationPayload.Device(requestContext));
}
