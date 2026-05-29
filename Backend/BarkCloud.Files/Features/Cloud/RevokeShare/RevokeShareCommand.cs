using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeShare;

public class RevokeShareCommand : IRequest<CloudEmpty>
{
    public Guid ShareId { get; set; }
}
