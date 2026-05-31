using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeUserShare;

public class RevokeUserShareCommand : IRequest<CloudEmpty>
{
    public Guid GrantId { get; set; }
}
