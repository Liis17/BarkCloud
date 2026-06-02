using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListSharedDirectory;

public class ListSharedDirectoryCommand : IRequest<ListSharedDirectoryResponse>
{
    public Guid DirectoryId { get; set; }
}
