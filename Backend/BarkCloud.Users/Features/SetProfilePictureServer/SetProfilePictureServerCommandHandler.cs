using BarkCloud.Proto.Users;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.SetProfilePictureServer;

public class SetProfilePictureServerCommandHandler : IRequestHandler<SetProfilePictureServerCommand, SetProfilePictureServerResponse>
{
    private readonly IUsersStorage _usersStorage;
    private readonly UserInfoQueueSender _userInfoQueueSender;
    private readonly ILogger<SetProfilePictureServerCommandHandler> _logger;

    public SetProfilePictureServerCommandHandler(
        IUsersStorage usersStorage,
        UserInfoQueueSender userInfoQueueSender,
        ILogger<SetProfilePictureServerCommandHandler> logger)
    {
        _usersStorage = usersStorage;
        _userInfoQueueSender = userInfoQueueSender;
        _logger = logger;
    }

    public async Task<SetProfilePictureServerResponse> Handle(SetProfilePictureServerCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Установка аватара для пользователя {UserId} (серверный)", request.UserId);

        await _usersStorage.UpdateProfilePicture(request.UserId, request.ProfilePictureUrl, request.ProfilePicturePreviewUrl);

        await _userInfoQueueSender.UserChangedAvatarEvent(request.UserId, request.ProfilePictureUrl, request.ProfilePicturePreviewUrl);

        _logger.LogInformation("Аватар успешно установлен для пользователя {UserId} (серверный)", request.UserId);

        return new SetProfilePictureServerResponse();
    }
}
