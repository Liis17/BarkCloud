using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeFolderUserShare;

public class RevokeFolderUserShareCommand : IRequest<CloudEmpty>
{
    public Guid GrantId { get; set; }
}
