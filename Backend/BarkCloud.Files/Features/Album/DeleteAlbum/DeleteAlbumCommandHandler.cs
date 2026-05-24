using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.Album.DeleteAlbum;

public class DeleteAlbumCommandHandler : IRequestHandler<DeleteAlbumCommand, CloudEmpty>
{
    private readonly AlbumStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteAlbumCommandHandler> _logger;

    public DeleteAlbumCommandHandler(
        AlbumStorage storage,
        UserContext userContext,
        ILogger<DeleteAlbumCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(DeleteAlbumCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var album = await _storage.GetAlbum(request.AlbumId, cancellationToken);
        if (album is null)
            throw new AlbumNotFoundException();
        if (album.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        // Удаляем альбом и его элементы. Сами файлы (блобы) остаются в облаке.
        await _storage.RemoveAlbum(album, cancellationToken);

        _logger.LogInformation("Удалён альбом {AlbumId} (Owner: {OwnerId})", album.Id, ownerId);

        return new CloudEmpty();
    }
}
