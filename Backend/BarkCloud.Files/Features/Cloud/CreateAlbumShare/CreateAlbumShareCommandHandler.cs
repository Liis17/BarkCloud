using System.Security.Cryptography;

using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using DomainAlbumShareLink = BarkCloud.Files.Domain.AlbumShareLink;

namespace BarkCloud.Files.Features.Cloud.CreateAlbumShare;

public class CreateAlbumShareCommandHandler : IRequestHandler<CreateAlbumShareCommand, AlbumShareInfo>
{
    private readonly IAlbumShareStorage _storage;
    private readonly IAlbumStorage _albums;
    private readonly UserContext _userContext;
    private readonly ILogger<CreateAlbumShareCommandHandler> _logger;

    public CreateAlbumShareCommandHandler(
        IAlbumShareStorage storage,
        IAlbumStorage albums,
        UserContext userContext,
        ILogger<CreateAlbumShareCommandHandler> logger)
    {
        _storage = storage;
        _albums = albums;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<AlbumShareInfo> Handle(CreateAlbumShareCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Публиковать можно только свой альбом.
        var album = await _albums.GetAlbum(request.AlbumId, cancellationToken);
        if (album is null)
            throw new AlbumNotFoundException();
        if (album.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        // Идемпотентность: один публичный шар на альбом — вернём существующий.
        var existing = await _storage.GetByAlbum(ownerId, request.AlbumId, cancellationToken);
        if (existing is not null)
            return ToGrpc(existing);

        var share = new DomainAlbumShareLink
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            AlbumId = request.AlbumId,
            Token = GenerateToken(),
            Name = string.IsNullOrWhiteSpace(request.Name) ? album.Name : request.Name,
            CreatedAt = DateTime.UtcNow,
            ClickCount = 0
        };

        await _storage.Add(share, cancellationToken);

        _logger.LogInformation("Создана публичная ссылка на альбом {ShareId} (album {AlbumId}, Owner: {OwnerId})",
            share.Id, request.AlbumId, ownerId);

        return ToGrpc(share);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static AlbumShareInfo ToGrpc(DomainAlbumShareLink share)
    {
        return new AlbumShareInfo
        {
            Id = share.Id.ToString(),
            Token = share.Token,
            AlbumId = share.AlbumId.ToString(),
            Name = share.Name,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(share.CreatedAt, DateTimeKind.Utc)),
            ClickCount = share.ClickCount
        };
    }
}
