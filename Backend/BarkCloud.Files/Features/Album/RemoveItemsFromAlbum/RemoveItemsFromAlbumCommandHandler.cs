using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.Album.RemoveItemsFromAlbum;

public class RemoveItemsFromAlbumCommandHandler : IRequestHandler<RemoveItemsFromAlbumCommand, CloudEmpty>
{
    private readonly IAlbumStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<RemoveItemsFromAlbumCommandHandler> _logger;

    public RemoveItemsFromAlbumCommandHandler(
        IAlbumStorage storage,
        UserContext userContext,
        ILogger<RemoveItemsFromAlbumCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RemoveItemsFromAlbumCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var album = await _storage.GetAlbum(request.AlbumId, cancellationToken);
        if (album is null)
            throw new AlbumNotFoundException();
        if (album.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        var fileIds = request.FileIds.Distinct().ToList();
        if (fileIds.Count == 0)
            return new CloudEmpty();

        var removed = await _storage.RemoveItems(album.Id, fileIds, cancellationToken);

        // Если убрали текущую обложку — переустанавливаем на первый оставшийся элемент (или сбрасываем).
        if (removed > 0 && album.CoverFileId.HasValue && fileIds.Contains(album.CoverFileId.Value))
        {
            var first = await _storage.GetFirstItem(album.Id, cancellationToken);
            album.CoverFileId = first?.FileId;
            album.UpdatedAt = DateTime.UtcNow;
            await _storage.UpdateAlbum(album, cancellationToken);
        }

        _logger.LogInformation(
            "Из альбома {AlbumId} удалено {Count} элементов (Owner: {OwnerId})",
            album.Id, removed, ownerId);

        return new CloudEmpty();
    }
}
