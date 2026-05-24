using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Exceptions.Users;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.ChangeBio;

public class ChangeBioCommandHandler : IRequestHandler<ChangeBioCommand>
{
    private const int MaxBioLength = 200;

    private readonly UserContext _userContext;
    private readonly UsersStorage _usersStorage;
    private readonly UserInfoQueueSender _userInfoQueueSender;
    private readonly ILogger<ChangeBioCommandHandler> _logger;

    public ChangeBioCommandHandler(UserContext userContext, UsersStorage usersStorage,
        UserInfoQueueSender userInfoQueueSender, ILogger<ChangeBioCommandHandler> logger)
    {
        _userContext = userContext;
        _usersStorage = usersStorage;
        _userInfoQueueSender = userInfoQueueSender;
        _logger = logger;
    }

    public async Task Handle(ChangeBioCommand request, CancellationToken cancellationToken)
    {
        var bio = request.Bio?.Trim();

        _logger.LogInformation(
            "Начало изменения bio для пользователя {UserId}",
            _userContext.UserId
        );

        if (bio is { Length: > MaxBioLength })
        {
            _logger.LogWarning("Bio пользователя {UserId} превышает {Max} символов", _userContext.UserId, MaxBioLength);
            throw new BioTooLongException();
        }

        var stored = string.IsNullOrEmpty(bio) ? null : bio;

        await _usersStorage.ChangeBio(_userContext.UserId, stored);

        _logger.LogDebug(
            "Отправка события об изменении bio в очередь RabbitMQ для пользователя {UserId}",
            _userContext.UserId
        );

        await _userInfoQueueSender.BioChangedEvent(_userContext.UserId, stored ?? string.Empty);

        _logger.LogInformation(
            "Bio успешно изменено для пользователя {UserId}",
            _userContext.UserId
        );
    }
}
