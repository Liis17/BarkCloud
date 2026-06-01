using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyOutgoingShares;

public class ListMyOutgoingSharesCommandHandler : IRequestHandler<ListMyOutgoingSharesCommand, ListMyOutgoingSharesResponse>
{
    private readonly IGrantStorage _grantStorage;
    private readonly UserContext _userContext;

    public ListMyOutgoingSharesCommandHandler(IGrantStorage grantStorage, UserContext userContext)
    {
        _grantStorage = grantStorage;
        _userContext = userContext;
    }

    public async Task<ListMyOutgoingSharesResponse> Handle(ListMyOutgoingSharesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var grants = await _grantStorage.ListByOwnerFile(ownerId, request.FileId, cancellationToken);

        var response = new ListMyOutgoingSharesResponse();
        foreach (var g in grants)
        {
            response.Items.Add(new OutgoingShareEntry
            {
                GrantId = g.Id.ToString(),
                RecipientUserId = g.RecipientId,
                SharedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(g.CreatedAt, DateTimeKind.Utc))
            });
        }

        return response;
    }
}
