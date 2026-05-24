using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Users;
using BarkCloud.Users.Mapping;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.Privacy.GetPrivacySettings;

public class GetPrivacySettingsQueryHandler(
    UsersStorage usersStorage,
    UserContext userContext,
    ILogger<GetPrivacySettingsQueryHandler> logger)
    : IRequestHandler<GetPrivacySettingsQuery, GetPrivacySettingsResponse>
{
    public async Task<GetPrivacySettingsResponse> Handle(GetPrivacySettingsQuery request, CancellationToken cancellationToken)
    {
        logger.LogDebug("Получение настроек приватности для пользователя {UserId}", userContext.UserId);

        var privacy = await usersStorage.GetOrCreatePrivacy(userContext.UserId);

        return new GetPrivacySettingsResponse { Settings = privacy.ToGrpc() };
    }
}
