using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.MoveFileEntries;

public class MoveFileEntriesCommand : IRequest<MoveFileEntriesResponse>
{
    public IReadOnlyCollection<Guid> EntryIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Целевая директория. null означает перемещение в корень владельца.
    /// </summary>
    public Guid? NewDirectoryId { get; set; }
}
