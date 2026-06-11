using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeShare;

public class RevokeShareCommandHandler : IRequestHandler<RevokeShareCommand, CloudEmpty>
{
    private readonly IShareStorage _storage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<RevokeShareCommandHandler> _logger;

    public RevokeShareCommandHandler(
        IShareStorage storage,
        UserContext userContext,
        ILogger<RevokeShareCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _storage = storage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RevokeShareCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Удаляем только ссылку этого владельца — идемпотентно, без ошибки если её уже нет.
        var share = await _storage.Get(ownerId, request.ShareId, cancellationToken);
        var removed = await _storage.Remove(ownerId, request.ShareId, cancellationToken);
        if (removed > 0)
        {
            _logger.LogInformation("Отозвана публичная ссылка {ShareId} (Owner: {OwnerId})", request.ShareId, ownerId);
            if (share is not null)
            {
                await _activity.AddAsync(
                    ownerId,
                    share.FileId,
                    ownerId,
                    FileActivityKind.ShareRevoked,
                    "Публичная ссылка отозвана",
                    details: new { shareId = share.Id, share.Name },
                    cancellationToken: cancellationToken);
            }
        }

        return new CloudEmpty();
    }
}
