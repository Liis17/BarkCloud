using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DomainAlbum = BarkCloud.Files.Domain.Album;

namespace BarkCloud.Files.Features.Album.CreateAlbum;

public class CreateAlbumCommandHandler : IRequestHandler<CreateAlbumCommand, AlbumInfo>
{
    private readonly AlbumStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<CreateAlbumCommandHandler> _logger;

    public CreateAlbumCommandHandler(
        AlbumStorage storage,
        UserContext userContext,
        ILogger<CreateAlbumCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<AlbumInfo> Handle(CreateAlbumCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var name = (request.Name ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name))
            throw new AlbumNameConflictException();

        if (await _storage.AlbumNameExists(ownerId, name, cancellationToken))
            throw new AlbumNameConflictException();

        var now = DateTime.UtcNow;
        var album = new DomainAlbum
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        await _storage.AddAlbum(album, cancellationToken);

        _logger.LogInformation("Создан альбом {AlbumId} (Name: {Name}, Owner: {OwnerId})", album.Id, album.Name, ownerId);

        return album.ToGrpc(0);
    }
}
