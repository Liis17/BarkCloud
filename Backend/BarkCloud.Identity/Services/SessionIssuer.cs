using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Queue.Notifications;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Identity.Services;

/// <summary>Устройство, на которое выпускается сессия. <see cref="AppName"/> — готовая строка «приложение + версия».</summary>
public record SessionDevice(string DeviceId, string? DeviceName, string? OperationSystem, string AppName, string? IpAddress);

// Выпуск сессии (refresh + access) для уже аутентифицированного пользователя:
// создаёт токены, регистрирует устройство, ставит в очередь уведомление о входе. Общий хвост входа —
// им пользуются вход паролем (AuthCommandHandler), вход по ключу (WebAuthn) и серверное создание сессии.
// Локальные изменения и событие outbox фиксируются одной транзакцией.
public class SessionIssuer(
    UsersServerApi.UsersServerApiClient usersClient,
    IMediator mediator,
    INotificationOutbox notificationOutbox,
    IRefreshTokensStorage refreshTokensStorage,
    IdentityContext context,
    RequestContext requestContext,
    LocationClient locationClient,
    MetricsCollector metrics,
    ILogger<SessionIssuer> logger)
{
    private const int ExpDaysRefreshToken = 9999;

    /// <summary>Вход пользователя: устройство берётся из заголовков запроса.</summary>
    public virtual async Task<AuthResponse> IssueAsync(long userId, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? completeAuthentication = null)
    {
        // Если DeviceId не передан, генерируем временный.
        var deviceId = string.IsNullOrEmpty(requestContext.DeviceId)
            ? Guid.NewGuid().ToString()
            : requestContext.DeviceId;

        var device = new SessionDevice(
            deviceId,
            requestContext.DeviceName,
            requestContext.OperationSystem,
            $"{requestContext.AppName} v.{requestContext.AppVersion}",
            requestContext.IpAddress);

        var response = await IssueAsyncCore(userId, device, cancellationToken, completeAuthentication);

        metrics.Increment("auth_login_success");

        return response;
    }

    /// <summary>Сессия для явно заданного устройства (серверное создание сессии).</summary>
    public virtual async Task<AuthResponse> IssueAsync(long userId, SessionDevice device, CancellationToken cancellationToken)
    {
        var locationInfo = await locationClient.GetLocationString(device.IpAddress);
        return await IssueAsyncCore(userId, device, cancellationToken, null, locationInfo);
    }

    private async Task<AuthResponse> IssueAsyncCore(long userId, SessionDevice device, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? completeAuthentication, string? knownLocation = null)
    {
        var locationInfo = knownLocation ?? await locationClient.GetLocationString(device.IpAddress);
        var refreshTokenString = RefreshTokenGenerator.GenerateRefreshToken();
        CreateTokenResponse accessTokenResponse;
        var notificationEnqueued = false;

        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                if (completeAuthentication is not null)
                {
                    await completeAuthentication(cancellationToken);
                }

                await refreshTokensStorage.DeleteRefreshTokensByDeviceIdSafe(device.DeviceId, userId, cancellationToken);
                await refreshTokensStorage.CreateNewRefreshToken(refreshTokenString, userId, device.DeviceId,
                    ExpDaysRefreshToken, cancellationToken);
                accessTokenResponse = await mediator.Send(new CreateTokenCommand
                {
                    RefreshToken = refreshTokenString,
                    DeferSuccessTelemetry = true
                }, cancellationToken);

                notificationEnqueued = await notificationOutbox.EnqueueAsync(
                    userId,
                    NotificationType.SuccessfulLogin,
                    "Успешный вход в аккаунт",
                    NotificationPayload.Device(device.IpAddress, device.DeviceName, device.OperationSystem, device.AppName, locationInfo),
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
                throw;
            }
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }

        if (notificationEnqueued)
        {
            metrics.Increment("notification_outbox_enqueued");
        }

        metrics.Increment("sessions_created");
        metrics.Increment("tokens_refreshed");

        try
        {
            await usersClient.RegisterDeviceAsync(new RegisterDeviceRequest
            {
                DeviceId = device.DeviceId,
                UserId = userId,
                OriginalName = device.DeviceName ?? "Unknown",
                AppName = device.AppName,
                OperationSystem = device.OperationSystem ?? string.Empty,
                Location = locationInfo
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось зарегистрировать устройство {DeviceId} для пользователя {UserId}",
                device.DeviceId, userId);
        }

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
