using BarkCloud.Files.Features.Cloud.CreateAlbumShare;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyAlbumShares;

public class ListMyAlbumSharesCommandHandler : IRequestHandler<ListMyAlbumSharesCommand, ListMyAlbumSharesResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly IAlbumShareStorage _storage;
    private readonly IAlbumStorage _albums;
    private readonly AlbumViewBuilder _viewBuilder;
    private readonly UserContext _userContext;

    public ListMyAlbumSharesCommandHandler(
        IAlbumShareStorage storage,
        IAlbumStorage albums,
        AlbumViewBuilder viewBuilder,
        UserContext userContext)
    {
        _storage = storage;
        _albums = albums;
        _viewBuilder = viewBuilder;
        _userContext = userContext;
    }

    public async Task<ListMyAlbumSharesResponse> Handle(ListMyAlbumSharesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var shares = await _storage.ListPage(
            ownerId, request.CursorCreatedAt, request.CursorAlbumShareId, limit, cancellationToken);

        var hasMore = shares.Count > limit;
        var page = hasMore ? shares.Take(limit).ToList() : shares;

        // Превью обложек: грузим альбомы страницы и собираем их view (cover_preview_url) батчем.
        var albums = new List<Domain.Album>();
        foreach (var id in page.Select(s => s.AlbumId).Distinct())
        {
            var album = await _albums.GetAlbum(id, cancellationToken);
            if (album is not null)
                albums.Add(album);
        }
        var coverByAlbum = (await _viewBuilder.BuildAsync(albums, cancellationToken))
            .ToDictionary(v => v.Id, v => v.CoverPreviewUrl);

        var response = new ListMyAlbumSharesResponse();
        foreach (var s in page)
        {
            coverByAlbum.TryGetValue(s.AlbumId.ToString(), out var coverUrl);
            response.Shares.Add(CreateAlbumShareCommandHandler.ToGrpc(s, coverUrl));
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorAlbumShareId = last.Id.ToString();
        }

        return response;
    }
}
