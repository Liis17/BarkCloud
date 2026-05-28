using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Users;
using BarkCloud.Users.Mapping;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.Privacy.UpdatePrivacySettings;

public class UpdatePrivacySettingsCommandHandler(
    IUsersStorage usersStorage,
    UserContext userContext,
    ILogger<UpdatePrivacySettingsCommandHandler> logger)
    : IRequestHandler<UpdatePrivacySettingsCommand, UpdatePrivacySettingsResponse>
{
    public async Task<UpdatePrivacySettingsResponse> Handle(UpdatePrivacySettingsCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Обновление настроек приватности для пользователя {UserId}", userContext.UserId);

        var privacy = await usersStorage.UpdatePrivacy(
            userContext.UserId,
            request.ProfileVisibility,
            request.EmailVisibility,
            request.LastSeenVisibility,
            request.SearchableByUsername);

        return new UpdatePrivacySettingsResponse { Settings = privacy.ToGrpc() };
    }
}
