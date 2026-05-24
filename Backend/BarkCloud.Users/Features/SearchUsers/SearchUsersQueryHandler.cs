using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Users;
using BarkCloud.Users.Mapping;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.SearchUsers;

public class SearchUsersQueryHandler : IRequestHandler<SearchUsersQuery, SearchUsersResponse>
{
    private const int MinQueryLength = 2;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    private readonly UsersStorage _usersStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<SearchUsersQueryHandler> _logger;

    public SearchUsersQueryHandler(UsersStorage usersStorage, UserContext userContext,
        ILogger<SearchUsersQueryHandler> logger)
    {
        _usersStorage = usersStorage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<SearchUsersResponse> Handle(SearchUsersQuery request, CancellationToken cancellationToken)
    {
        var query = request.Query?.Trim() ?? string.Empty;

        if (query.Length < MinQueryLength)
        {
            _logger.LogDebug("Поисковый запрос короче {Min} символов — возвращаем пустой результат", MinQueryLength);
            return new SearchUsersResponse();
        }

        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        _logger.LogDebug(
            "Поиск пользователей по запросу '{Query}' (limit {Limit}), запросил {UserId}",
            query, limit, _userContext.UserId
        );

        var users = await _usersStorage.SearchUsers(query, _userContext.UserId, limit);

        var response = new SearchUsersResponse();
        response.Users.AddRange(users.Select(u => u.ToGrpc()));

        return response;
    }
}
