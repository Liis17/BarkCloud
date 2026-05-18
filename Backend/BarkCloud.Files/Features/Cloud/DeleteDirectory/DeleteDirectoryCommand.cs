using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.DeleteDirectory;

public class DeleteDirectoryCommand : IRequest<CloudEmpty>
{
    public Guid DirectoryId { get; set; }
}
