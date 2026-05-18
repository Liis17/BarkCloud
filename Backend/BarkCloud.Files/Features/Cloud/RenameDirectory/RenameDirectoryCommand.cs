using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RenameDirectory;

public class RenameDirectoryCommand : IRequest<CloudEmpty>
{
    public Guid DirectoryId { get; set; }

    public string NewName { get; set; } = "";
}
