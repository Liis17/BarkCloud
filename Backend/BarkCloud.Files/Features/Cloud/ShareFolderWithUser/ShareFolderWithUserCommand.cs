using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ShareFolderWithUser;

public class ShareFolderWithUserCommand : IRequest<CloudEmpty>
{
    public Guid DirectoryId { get; set; }

    public long RecipientUserId { get; set; }
}
