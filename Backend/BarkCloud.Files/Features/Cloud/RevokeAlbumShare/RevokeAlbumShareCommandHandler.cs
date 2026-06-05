using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeAlbumShare;

/// <summary>Отзывает публичность альбома. Идемпотентно (нет строки / не владелец → no-op).</summary>
public class RevokeAlbumShareCommandHandler : IRequestHandler<RevokeAlbumShareCommand, CloudEmpty>
{
    private readonly IAlbumShareStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<RevokeAlbumShareCommandHandler> _logger;

    public RevokeAlbumShareCommandHandler(
        IAlbumShareStorage storage,
        UserContext userContext,
        ILogger<RevokeAlbumShareCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RevokeAlbumShareCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var removed = await _storage.Remove(ownerId, request.AlbumShareId, cancellationToken);
        if (removed > 0)
            _logger.LogInformation("Отозвана публичная ссылка на альбом {ShareId} (Owner: {OwnerId})",
                request.AlbumShareId, ownerId);

        return new CloudEmpty();
    }
}
