using BarkCloud.Proto.Users;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.UpdateProfileServer;

public class UpdateProfileServerCommandHandler : IRequestHandler<UpdateProfileServerCommand, UpdateProfileServerResponse>
{
    private readonly UsersStorage _usersStorage;
    private readonly UserInfoQueueSender _userInfoQueueSender;
    private readonly ILogger<UpdateProfileServerCommandHandler> _logger;

    public UpdateProfileServerCommandHandler(
        UsersStorage usersStorage,
        UserInfoQueueSender userInfoQueueSender,
        ILogger<UpdateProfileServerCommandHandler> logger)
    {
        _usersStorage = usersStorage;
        _userInfoQueueSender = userInfoQueueSender;
        _logger = logger;
    }

    public async Task<UpdateProfileServerResponse> Handle(UpdateProfileServerCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Обновление профиля пользователя {UserId} (серверный)", request.UserId);

        var current = await _usersStorage.GetById(request.UserId);
        if (current is null)
            throw new InvalidOperationException($"Пользователь {request.UserId} не найден");

        var firstName = request.FirstName?.Trim();
        var lastName = request.LastName?.Trim();
        var username = request.Username?.Trim();

        var nameChanged = !string.IsNullOrEmpty(firstName) && (firstName != current.FirstName || lastName != current.LastName);
        var usernameChanged = !string.IsNullOrEmpty(username) && username != current.Username;

        if (nameChanged)
        {
            await _usersStorage.ChangeName(request.UserId, firstName!, lastName ?? string.Empty);
            await _userInfoQueueSender.NameChangedEvent(request.UserId, firstName!, lastName ?? string.Empty);
            _logger.LogInformation("Имя пользователя {UserId} обновлено: {First} {Last}", request.UserId, firstName, lastName);
        }

        if (usernameChanged)
        {
            await _usersStorage.ChangeUsername(request.UserId, username!);
            await _userInfoQueueSender.UsernameChangedEvent(request.UserId, username!);
            _logger.LogInformation("Username пользователя {UserId} обновлён: {Username}", request.UserId, username);
        }

        return new UpdateProfileServerResponse();
    }
}
