using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Identity.Services;

// Выпуск сессии (refresh + access) для уже аутентифицированного пользователя:
// создаёт токены, регистрирует устройство, шлёт уведомление о входе. Общий хвост входа —
// используется входом по ключу (WebAuthn) так же, как паролем в AuthCommandHandler.
public class SessionIssuer(
    UsersServerApi.UsersServerApiClient usersClient,
    IMediator mediator,
    NotificationQueueSender notificationQueueSender,
    IRefreshTokensStorage refreshTokensStorage,
    RequestContext requestContext,
    LocationClient locationClient,
    MetricsCollector metrics,
    ILogger<SessionIssuer> logger)
{
    private const int ExpDaysRefreshToken = 9999;

    public async Task<AuthResponse> IssueAsync(long userId, CancellationToken cancellationToken)
    {
        // Если DeviceId не передан, генерируем временный (как в AuthCommandHandler).
        var deviceId = string.IsNullOrEmpty(requestContext.DeviceId)
            ? Guid.NewGuid().ToString()
            : requestContext.DeviceId;

        await refreshTokensStorage.DeleteRefreshTokensByDeviceIdSafe(deviceId, userId);

        var refreshTokenString = RefreshTokenGenerator.GenerateRefreshToken();
        await refreshTokensStorage.CreateNewRefreshToken(refreshTokenString, userId, deviceId, ExpDaysRefreshToken);

        var accessTokenResponse = await mediator.Send(new CreateTokenCommand { RefreshToken = refreshTokenString }, cancellationToken);

        var locationInfo = await locationClient.GetLocationString(requestContext.IpAddress);

        var appName = $"{requestContext.AppName} v.{requestContext.AppVersion}";

        try
        {
            await usersClient.RegisterDeviceAsync(new RegisterDeviceRequest
            {
                DeviceId = deviceId,
                UserId = userId,
                OriginalName = requestContext.DeviceName ?? "Unknown",
                AppName = appName,
                OperationSystem = requestContext.OperationSystem ?? string.Empty,
                Location = locationInfo
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось зарегистрировать устройство {DeviceId} для пользователя {UserId}",
                deviceId, userId);
        }

        try
        {
            var userContacts = await usersClient.GetUserContactsAsync(new GetUserContactsRequest { UserId = userId });

            var notification = new EmailNotification
            {
                OwnerId = userId,
                Address = userContacts.Contact.Email,
                CreatedAt = DateTime.UtcNow,
                Payload = new Dictionary<string, string>
                {
                    {"username", userContacts.User.Username},
                    {"ip", requestContext.IpAddress ?? string.Empty},
                    {"devicename", requestContext.DeviceName ?? string.Empty},
                    {"os", requestContext.OperationSystem ?? string.Empty},
                    {"location", locationInfo},
                    {"appname", appName},
                    {"datetime", DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss")}
                },
                ServiceId = ServiceId.Identity,
                Title = "Успешный вход в аккаунт",
                Type = NotificationType.SuccessfulLogin
            };

            await notificationQueueSender.SendNotification(notification);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Не удалось отправить уведомление об успешном входе для пользователя {UserId}", userId);
        }

        metrics.Increment("auth_login_success");
        metrics.Increment("sessions_created");

        return new AuthResponse
        {
            AccessToken = accessTokenResponse.AccessToken,
            RefreshToken = new Token
            {
                Value = refreshTokenString,
                ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(ExpDaysRefreshToken))
            }
        };
    }
}
