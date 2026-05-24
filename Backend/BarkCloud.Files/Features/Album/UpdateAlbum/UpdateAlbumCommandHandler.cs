using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.Album.UpdateAlbum;

public class UpdateAlbumCommandHandler : IRequestHandler<UpdateAlbumCommand, AlbumInfo>
{
    private readonly AlbumStorage _storage;
    private readonly UploadedFilesStorage _filesStorage;
    private readonly AlbumViewBuilder _viewBuilder;
    private readonly UserContext _userContext;
    private readonly ILogger<UpdateAlbumCommandHandler> _logger;

    public UpdateAlbumCommandHandler(
        AlbumStorage storage,
        UploadedFilesStorage filesStorage,
        AlbumViewBuilder viewBuilder,
        UserContext userContext,
        ILogger<UpdateAlbumCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _viewBuilder = viewBuilder;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<AlbumInfo> Handle(UpdateAlbumCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var album = await _storage.GetAlbum(request.AlbumId, cancellationToken);
        if (album is null)
            throw new AlbumNotFoundException();
        if (album.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new AlbumNameConflictException();

            if (name != album.Name && await _storage.AlbumNameExists(ownerId, name, cancellationToken))
                throw new AlbumNameConflictException();

            album.Name = name;
        }

        if (request.Description is not null)
            album.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        if (request.UpdateCover)
        {
            if (request.CoverFileId is null)
            {
                album.CoverFileId = null;
            }
            else
            {
                var coverFile = await _filesStorage.GetFile(request.CoverFileId.Value);
                if (coverFile is null || !coverFile.Uploaders.Contains(ownerId))
                    throw new CloudAccessDeniedException();

                album.CoverFileId = coverFile.Id;
            }
        }

        album.UpdatedAt = DateTime.UtcNow;
        await _storage.UpdateAlbum(album, cancellationToken);

        _logger.LogInformation("Обновлён альбом {AlbumId} (Owner: {OwnerId})", album.Id, ownerId);

        var views = await _viewBuilder.BuildAsync(new[] { album }, cancellationToken);
        return views[0];
    }
}
