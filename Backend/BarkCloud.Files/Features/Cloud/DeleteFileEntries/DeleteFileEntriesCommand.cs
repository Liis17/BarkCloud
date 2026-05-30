using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.DeleteFileEntries;

public class DeleteFileEntriesCommand : IRequest<DeleteFileEntriesResponse>
{
    public IReadOnlyCollection<Guid> EntryIds { get; set; } = Array.Empty<Guid>();
}
