using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Users.Mapping;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.GetUser;

public class GetUserQueryHandler : IRequestHandler<GetUserQuery, GetUserResponse>
{
    private readonly UsersStorage _usersStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<GetUserQueryHandler> _logger;

    public GetUserQueryHandler(
        UsersStorage usersStorage,
        UserContext userContext,
        ILogger<GetUserQueryHandler> logger)
    {
        _usersStorage = usersStorage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<GetUserResponse> Handle(GetUserQuery request, CancellationToken cancellationToken)
    {
        var userId = request.UserId ?? _userContext.UserId;

        _logger.LogDebug(
            "Получение информации о пользователе {UserId}. Запросил: {RequesterId}",
            userId,
            _userContext.UserId
        );

        var user = await _usersStorage.GetById(userId);

        if (user == null)
        {
            _logger.LogWarning(
                "Пользователь {UserId} не найден",
                userId
            );
            throw new UserNotFoundException();
        }

        _logger.LogInformation(
            "Информация о пользователе {UserId} ({Username}) успешно получена",
            userId,
            user.Username
        );

        return new GetUserResponse { User = user.ToGrpc() };
    }
}
