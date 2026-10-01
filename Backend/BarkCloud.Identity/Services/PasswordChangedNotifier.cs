using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

namespace BarkCloud.Identity.Services;

// Письмо «Пароль успешно изменен» — общий хвост смены пароля (SetPassword) и сброса (ConfirmResetPassword).
public class PasswordChangedNotifier(
    UsersServerApi.UsersServerApiClient usersClient,
    NotificationQueueSender notificationQueueSender,
    LocationClient locationClient,
    RequestContext requestContext,
    ILogger<PasswordChangedNotifier> logger)
{
    public virtual async Task NotifyAsync(long userId)
    {
        var userInfo = await usersClient.GetByIdAsync(new GetByIdRequest { UserId = userId });
        var userContacts = await usersClient.GetUserContactsAsync(new GetUserContactsRequest { UserId = userId });

        var locationInfo = await locationClient.GetLocationString(requestContext.IpAddress);

        var passwordChangedNotification = new EmailNotification
        {
            OwnerId = userId,
            Address = userContacts.Contact.Email,
            CreatedAt = DateTime.UtcNow,
            Payload = new Dictionary<string, string>
            {
                {"username", userInfo.User.Username},
                {"ip", requestContext.IpAddress ?? string.Empty},
                {"devicename", requestContext.DeviceName ?? string.Empty},
                {"os", requestContext.OperationSystem ?? string.Empty},
                {"location", locationInfo},
                {"appname", $"{requestContext.AppName} v.{requestContext.AppVersion}"},
                {"datetime", DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss")}

            },
            ServiceId = ServiceId.Identity,
            Title = "Пароль успешно изменен",
            Type = NotificationType.PasswordChanged
        };

        logger.LogDebug(
            "Отправка уведомления об изменении пароля на адрес {Email}",
            userContacts.Contact.Email
        );

        await notificationQueueSender.SendNotification(passwordChangedNotification);
    }
}
