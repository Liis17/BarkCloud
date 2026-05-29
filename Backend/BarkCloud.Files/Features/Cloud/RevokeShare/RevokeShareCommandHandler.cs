using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeShare;

public class RevokeShareCommandHandler : IRequestHandler<RevokeShareCommand, CloudEmpty>
{
    private readonly ShareStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<RevokeShareCommandHandler> _logger;

    public RevokeShareCommandHandler(
        ShareStorage storage,
        UserContext userContext,
        ILogger<RevokeShareCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RevokeShareCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Удаляем только ссылку этого владельца — идемпотентно, без ошибки если её уже нет.
        var removed = await _storage.Remove(ownerId, request.ShareId, cancellationToken);
        if (removed > 0)
            _logger.LogInformation("Отозвана публичная ссылка {ShareId} (Owner: {OwnerId})", request.ShareId, ownerId);

        return new CloudEmpty();
    }
}
