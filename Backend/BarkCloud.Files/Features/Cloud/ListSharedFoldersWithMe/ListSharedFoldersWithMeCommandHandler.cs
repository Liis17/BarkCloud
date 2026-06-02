using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListSharedFoldersWithMe;

/// <summary>
/// Папки, доступные получателю («мне доступны» — папки). Удалённые папки пропускаются.
/// </summary>
public class ListSharedFoldersWithMeCommandHandler
    : IRequestHandler<ListSharedFoldersWithMeCommand, ListSharedFoldersWithMeResponse>
{
    private readonly IDirectoryGrantStorage _dirGrants;
    private readonly ICloudHierarchyStorage _hierarchy;
    private readonly UserContext _userContext;

    public ListSharedFoldersWithMeCommandHandler(
        IDirectoryGrantStorage dirGrants, ICloudHierarchyStorage hierarchy, UserContext userContext)
    {
        _dirGrants = dirGrants;
        _hierarchy = hierarchy;
        _userContext = userContext;
    }

    public async Task<ListSharedFoldersWithMeResponse> Handle(
        ListSharedFoldersWithMeCommand request, CancellationToken cancellationToken)
    {
        var recipientId = _userContext.UserId;
        var grants = await _dirGrants.ListByRecipient(recipientId, cancellationToken);

        var response = new ListSharedFoldersWithMeResponse();
        foreach (var g in grants)
        {
            var dir = await _hierarchy.GetDirectoryAsNoTracking(g.DirectoryId, cancellationToken);
            if (dir is null)
                continue; // папка удалена владельцем

            response.Items.Add(new SharedFolderEntry
            {
                GrantId = g.Id.ToString(),
                DirectoryId = g.DirectoryId.ToString(),
                Name = dir.Name,
                OwnerUserId = g.OwnerId,
                SharedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(g.CreatedAt, DateTimeKind.Utc))
            });
        }

        return response;
    }
}
