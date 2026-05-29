using BarkCloud.Files.Features.Cloud.CreateShare;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyShares;

public class ListMySharesCommandHandler : IRequestHandler<ListMySharesCommand, ListMySharesResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly ShareStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<ListMySharesCommandHandler> _logger;

    public ListMySharesCommandHandler(
        ShareStorage storage,
        UserContext userContext,
        ILogger<ListMySharesCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<ListMySharesResponse> Handle(ListMySharesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var page = await _storage.ListPage(ownerId, request.CursorCreatedAt, request.CursorShareId, limit, cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListMySharesResponse();
        foreach (var share in page)
            response.Shares.Add(CreateShareCommandHandler.ToGrpc(share));

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorShareId = last.Id.ToString();
        }

        _logger.LogDebug("ListMyShares: owner={Owner} returned={Count} hasMore={HasMore}", ownerId, response.Shares.Count, hasMore);

        return response;
    }
}
