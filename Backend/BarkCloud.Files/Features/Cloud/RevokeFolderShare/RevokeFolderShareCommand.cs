using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeFolderShare;

public class RevokeFolderShareCommand : IRequest<CloudEmpty>
{
    public Guid FolderShareId { get; set; }
}
