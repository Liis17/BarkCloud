using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.Album.RemoveItemsFromAlbum;

public class RemoveItemsFromAlbumCommandHandler : IRequestHandler<RemoveItemsFromAlbumCommand, CloudEmpty>
{
    private readonly IAlbumStorage _storage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<RemoveItemsFromAlbumCommandHandler> _logger;

    public RemoveItemsFromAlbumCommandHandler(
        IAlbumStorage storage,
        UserContext userContext,
        ILogger<RemoveItemsFromAlbumCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _storage = storage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
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

        if (removed > 0)
        {
            await _activity.AddManyAsync(fileIds.Select(fileId => FileActivityWriter.Create(
                ownerId,
                fileId,
                ownerId,
                FileActivityKind.AlbumRemoved,
                $"Убран из альбома «{album.Name}»",
                details: new { albumId = album.Id, album.Name })),
                cancellationToken);
        }

        return new CloudEmpty();
    }
}
