using BarkCloud.Files.Features.Cloud.CreateFolderShare;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyFolderShares;

public class ListMyFolderSharesCommandHandler : IRequestHandler<ListMyFolderSharesCommand, ListMyFolderSharesResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly IFolderShareStorage _storage;
    private readonly UserContext _userContext;

    public ListMyFolderSharesCommandHandler(IFolderShareStorage storage, UserContext userContext)
    {
        _storage = storage;
        _userContext = userContext;
    }

    public async Task<ListMyFolderSharesResponse> Handle(ListMyFolderSharesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var shares = await _storage.ListPage(
            ownerId, request.CursorCreatedAt, request.CursorFolderShareId, limit, cancellationToken);

        var hasMore = shares.Count > limit;
        var page = hasMore ? shares.Take(limit).ToList() : shares;

        var response = new ListMyFolderSharesResponse();
        foreach (var s in page)
            response.Shares.Add(CreateFolderShareCommandHandler.ToGrpc(s));

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorFolderShareId = last.Id.ToString();
        }

        return response;
    }
}
