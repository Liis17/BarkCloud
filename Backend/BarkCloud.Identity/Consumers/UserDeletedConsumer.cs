using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Shared.Queue.Identity;
using BarkCloud.Shared.Queue.Users;

using MassTransit;

namespace BarkCloud.Identity.Consumers;

/// <summary>
/// Реагирует на удаление аккаунта (событие из сервиса Users): отзывает все сессии
/// и удаляет связанные с пользователем данные Identity (пароль, 2FA, сбросы, коды).
/// </summary>
public class UserDeletedConsumer(
    RefreshTokensStorage refreshTokensStorage,
    PasswordsStorage passwordsStorage,
    AuthPropertiesStorage authPropertiesStorage,
    ResetPasswordsStorage resetPasswordsStorage,
    ConfirmationCodesStorage confirmationCodesStorage,
    IPublishEndpoint publishEndpoint,
    JwtSettings jwtSettings,
    MetricsCollector metrics,
    ILogger<UserDeletedConsumer> logger)
    : IConsumer<UserDeleted>
{
    public async Task Consume(ConsumeContext<UserDeleted> context)
    {
        var userId = context.Message.UserId;

        metrics.Increment("rabbitmq_events_consumed");
        metrics.Increment("user_deleted_received");

        logger.LogInformation("Получено событие удаления аккаунта: UserId={UserId}", userId);

        // Удаляем refresh-токены и отзываем access-токены по каждому устройству.
        var deviceIds = await refreshTokensStorage.DeleteAllByUserId(userId);

        foreach (var deviceId in deviceIds)
        {
            await publishEndpoint.Publish(new SessionRevokedEvent
            {
                UserId = userId,
                DeviceId = deviceId,
                AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(jwtSettings.ExpiryMinutes)
            });
        }

        // Удаляем остальные данные пользователя.
        await passwordsStorage.DeleteByUserId(userId);
        await authPropertiesStorage.DeleteByUserId(userId);
        await resetPasswordsStorage.DeleteByUserId(userId);
        await confirmationCodesStorage.DeleteByOwnerId(userId);

        metrics.Increment("accounts_cleaned_identity");

        logger.LogInformation(
            "Данные Identity для пользователя {UserId} удалены, отозвано сессий: {SessionsCount}",
            userId, deviceIds.Count);
    }
}
