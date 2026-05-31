using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeUserShare;

public class RevokeUserShareCommandHandler : IRequestHandler<RevokeUserShareCommand, CloudEmpty>
{
    private readonly IGrantStorage _grantStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<RevokeUserShareCommandHandler> _logger;

    public RevokeUserShareCommandHandler(
        IGrantStorage grantStorage,
        UserContext userContext,
        ILogger<RevokeUserShareCommandHandler> logger)
    {
        _grantStorage = grantStorage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RevokeUserShareCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Удаляем только грант этого владельца — идемпотентно, без ошибки если его уже нет / не его.
        var removed = await _grantStorage.Remove(ownerId, request.GrantId, cancellationToken);
        if (removed > 0)
            _logger.LogInformation("Отозван грант доступа {GrantId} (Owner: {OwnerId})", request.GrantId, ownerId);

        return new CloudEmpty();
    }
}
