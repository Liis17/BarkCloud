using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.CreateFolderShare;

public class CreateFolderShareCommand : IRequest<FolderShareInfo>
{
    public Guid DirectoryId { get; set; }

    public string Name { get; set; } = string.Empty;
}
