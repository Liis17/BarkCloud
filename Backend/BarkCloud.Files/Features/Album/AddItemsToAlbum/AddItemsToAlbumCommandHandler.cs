using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DomainAlbumItem = BarkCloud.Files.Domain.AlbumItem;
using MediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Features.Album.AddItemsToAlbum;

public class AddItemsToAlbumCommandHandler : IRequestHandler<AddItemsToAlbumCommand, CloudEmpty>
{
    private readonly AlbumStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<AddItemsToAlbumCommandHandler> _logger;

    public AddItemsToAlbumCommandHandler(
        AlbumStorage storage,
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        ILogger<AddItemsToAlbumCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(AddItemsToAlbumCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var album = await _storage.GetAlbum(request.AlbumId, cancellationToken);
        if (album is null)
            throw new AlbumNotFoundException();
        if (album.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        var requestedIds = request.FileIds.Distinct().ToList();
        if (requestedIds.Count == 0)
            return new CloudEmpty();

        var files = await _filesStorage.GetFiles(requestedIds);

        // Принимаем только фото/видео, принадлежащие пользователю.
        var eligible = files
            .Where(f => f.Uploaders.Contains(ownerId)
                        && (f.MediaKind == MediaKind.Photo || f.MediaKind == MediaKind.Video))
            .Select(f => f.Id)
            .ToList();

        // Если среди запрошенных есть чужой файл — отказываем (защита от «приватизации» по ID).
        if (files.Any(f => !f.Uploaders.Contains(ownerId)))
            throw new CloudAccessDeniedException();

        if (eligible.Count == 0)
            return new CloudEmpty();

        var existing = await _storage.GetExistingItemFileIds(album.Id, eligible, cancellationToken);
        var toAdd = eligible.Where(id => !existing.Contains(id)).ToList();
        if (toAdd.Count == 0)
            return new CloudEmpty();

        var now = DateTime.UtcNow;
        var items = toAdd.Select(fileId => new DomainAlbumItem
        {
            Id = Guid.NewGuid(),
            AlbumId = album.Id,
            FileId = fileId,
            OwnerId = ownerId,
            AddedAt = now
        });

        await _storage.AddItems(items, cancellationToken);

        // Если у альбома ещё нет обложки — берём первый добавленный файл.
        if (album.CoverFileId is null)
        {
            album.CoverFileId = toAdd[0];
            album.UpdatedAt = now;
            await _storage.UpdateAlbum(album, cancellationToken);
        }

        _logger.LogInformation(
            "В альбом {AlbumId} добавлено {Count} элементов (Owner: {OwnerId})",
            album.Id, toAdd.Count, ownerId);

        return new CloudEmpty();
    }
}
