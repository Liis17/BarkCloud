using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.GetDirectoryProperties;

public class GetDirectoryPropertiesCommand : IRequest<DirectoryProperties>
{
    public Guid DirectoryId { get; set; }
}
