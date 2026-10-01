using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Queue.Identity;

using MassTransit;

namespace BarkCloud.Web.Auth;

/// <summary>Отзыв сессии в Identity (logout, удаление устройства/аккаунта): пополняет кэш отзыва,
/// по которому <see cref="AuthGateway"/> отвергает уже выданные access-токены.</summary>
public sealed class SessionRevokedConsumer(
    TokenRevocationCache cache,
    ILogger<SessionRevokedConsumer> logger)
    : IConsumer<SessionRevokedEvent>
{
    public Task Consume(ConsumeContext<SessionRevokedEvent> context)
    {
        var msg = context.Message;
        logger.LogInformation(
            "Получено событие отзыва сессии: UserId={UserId}, DeviceId={DeviceId}",
            msg.UserId, msg.DeviceId);

        cache.Revoke(msg.UserId, msg.DeviceId, msg.AccessTokenExpiresAt);
        return Task.CompletedTask;
    }
}
