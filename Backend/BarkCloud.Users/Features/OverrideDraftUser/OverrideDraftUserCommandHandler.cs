using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.OverrideDraftUser;

public class OverrideDraftUserCommandHandler : IRequestHandler<OverrideDraftUserCommand, AddDraftUserResponse>
{
    private readonly IUsersStorage _usersStorage;
    private readonly ILogger<OverrideDraftUserCommandHandler> _logger;

    public OverrideDraftUserCommandHandler(IUsersStorage usersStorage, ILogger<OverrideDraftUserCommandHandler> logger)
    {
        _usersStorage = usersStorage;
        _logger = logger;
    }

    public async Task<AddDraftUserResponse> Handle(OverrideDraftUserCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim();
        var username = request.Username?.Trim();
        var firstName = request.FirstName?.Trim();
        var lastName = request.LastName?.Trim();

        _logger.LogInformation(
            "Перезапись черновика пользователя. Username: {Username}, Email: {Email}",
            username,
            email
        );

        var user = await _usersStorage.GetUserByEmail(email);

        if (user == null)
        {
            _logger.LogWarning(
                "Черновик пользователя не найден по Email {Email}",
                email
            );
            throw new UserNotFoundException();
        }

        if (!user.IsDraft)
            throw new EmailExistException();

        var owner = await _usersStorage.GetUserByUsername(username);
        if (owner is not null && owner.Id != user.Id)
            throw new UsernameExistException();

        _logger.LogDebug(
            "Обновление данных черновика пользователя {UserId}",
            user.Id
        );

        await _usersStorage.OverrideDraftUser(user.Id, username, firstName, lastName, email);

        _logger.LogInformation(
            "Черновик пользователя {UserId} ({Username}) успешно перезаписан",
            user.Id,
            username
        );

        return new AddDraftUserResponse { UserId = user.Id };
    }
}
