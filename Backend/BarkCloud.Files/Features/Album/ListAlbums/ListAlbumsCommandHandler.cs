using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Album.ListAlbums;

public class ListAlbumsCommandHandler : IRequestHandler<ListAlbumsCommand, ListAlbumsResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly AlbumStorage _storage;
    private readonly AlbumViewBuilder _viewBuilder;
    private readonly UserContext _userContext;
    private readonly ILogger<ListAlbumsCommandHandler> _logger;

    public ListAlbumsCommandHandler(
        AlbumStorage storage,
        AlbumViewBuilder viewBuilder,
        UserContext userContext,
        ILogger<ListAlbumsCommandHandler> logger)
    {
        _storage = storage;
        _viewBuilder = viewBuilder;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<ListAlbumsResponse> Handle(ListAlbumsCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var page = await _storage.ListAlbumsPage(ownerId, request.CursorUpdatedAt, request.CursorAlbumId, limit, cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListAlbumsResponse();
        if (page.Count == 0)
            return response;

        var views = await _viewBuilder.BuildAsync(page, cancellationToken);
        response.Albums.AddRange(views);

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorUpdatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.UpdatedAt, DateTimeKind.Utc));
            response.NextCursorAlbumId = last.Id.ToString();
        }

        _logger.LogDebug("ListAlbums: owner={Owner} returned={Count} hasMore={HasMore}", ownerId, page.Count, hasMore);

        return response;
    }
}
