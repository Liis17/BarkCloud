using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.DynamicFolder.DeleteDynamicFolder;

public class DeleteDynamicFolderCommand : IRequest<CloudEmpty>
{
    public Guid FolderId { get; set; }
}
