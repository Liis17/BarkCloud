using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyOutgoingFolderShares;

/// <summary>
/// Все исходящие гранты владельца на папки («я поделился» — папки): какие папки и кому отданы.
/// Папки, которые уже удалены, пропускаются.
/// </summary>
public class ListMyOutgoingFolderSharesCommandHandler
    : IRequestHandler<ListMyOutgoingFolderSharesCommand, ListMyOutgoingFolderSharesResponse>
{
    private readonly IDirectoryGrantStorage _dirGrants;
    private readonly ICloudHierarchyStorage _hierarchy;
    private readonly UserContext _userContext;

    public ListMyOutgoingFolderSharesCommandHandler(
        IDirectoryGrantStorage dirGrants, ICloudHierarchyStorage hierarchy, UserContext userContext)
    {
        _dirGrants = dirGrants;
        _hierarchy = hierarchy;
        _userContext = userContext;
    }

    public async Task<ListMyOutgoingFolderSharesResponse> Handle(
        ListMyOutgoingFolderSharesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var grants = await _dirGrants.ListByOwner(ownerId, cancellationToken);

        var nameByDir = new Dictionary<Guid, string?>();
        var response = new ListMyOutgoingFolderSharesResponse();
        foreach (var g in grants)
        {
            if (!nameByDir.TryGetValue(g.DirectoryId, out var name))
            {
                var dir = await _hierarchy.GetDirectoryAsNoTracking(g.DirectoryId, cancellationToken);
                name = dir?.Name;
                nameByDir[g.DirectoryId] = name;
            }
            if (name is null)
                continue; // папка удалена

            response.Items.Add(new OutgoingFolderShare
            {
                GrantId = g.Id.ToString(),
                DirectoryId = g.DirectoryId.ToString(),
                Name = name,
                RecipientUserId = g.RecipientId,
                SharedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(g.CreatedAt, DateTimeKind.Utc))
            });
        }

        return response;
    }
}
