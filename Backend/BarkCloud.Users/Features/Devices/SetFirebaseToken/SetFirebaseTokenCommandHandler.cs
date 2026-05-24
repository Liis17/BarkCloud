using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Users;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.Devices.SetFirebaseToken;

public class SetFirebaseTokenCommandHandler(
    DevicesStorage devicesStorage,
    UserContext userContext,
    ILogger<SetFirebaseTokenCommandHandler> logger)
    : IRequestHandler<SetFirebaseTokenCommand, SetFirebaseTokenResponse>
{
    public async Task<SetFirebaseTokenResponse> Handle(SetFirebaseTokenCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userContext.DeviceId) || !Guid.TryParse(userContext.DeviceId, out var deviceGuid))
        {
            logger.LogWarning("Не удалось определить текущее устройство для пользователя {UserId}", userContext.UserId);
            throw new InvalidOperationException("Текущее устройство не определено");
        }

        var token = string.IsNullOrEmpty(request.FirebaseToken) ? null : request.FirebaseToken;

        logger.LogInformation(
            "Сохранение push-токена для устройства {DeviceId} пользователя {UserId} (сброс: {IsReset})",
            deviceGuid, userContext.UserId, token is null);

        await devicesStorage.SetFirebaseToken(deviceGuid, userContext.UserId, token);

        return new SetFirebaseTokenResponse();
    }
}
