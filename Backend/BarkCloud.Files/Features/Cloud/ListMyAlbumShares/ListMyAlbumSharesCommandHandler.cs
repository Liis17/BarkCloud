using BarkCloud.Files.Features.Cloud.CreateAlbumShare;
using BarkCloud.Files.Persistence;
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
    private readonly UserContext _userContext;

    public ListMyAlbumSharesCommandHandler(IAlbumShareStorage storage, UserContext userContext)
    {
        _storage = storage;
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

        var response = new ListMyAlbumSharesResponse();
        foreach (var s in page)
            response.Shares.Add(CreateAlbumShareCommandHandler.ToGrpc(s));

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorAlbumShareId = last.Id.ToString();
        }

        return response;
    }
}
