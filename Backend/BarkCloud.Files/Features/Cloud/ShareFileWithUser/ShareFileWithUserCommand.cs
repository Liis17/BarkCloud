using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ShareFileWithUser;

public class ShareFileWithUserCommand : IRequest<CloudEmpty>
{
    public Guid FileId { get; set; }

    public long RecipientUserId { get; set; }
}
