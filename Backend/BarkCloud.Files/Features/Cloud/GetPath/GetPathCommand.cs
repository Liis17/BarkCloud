using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.GetPath;

public class GetPathCommand : IRequest<PathResponse>
{
    public Guid? DirectoryId { get; set; }

    public Guid? EntryId { get; set; }
}
