using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RenameFileEntry;

public class RenameFileEntryCommand : IRequest<CloudEmpty>
{
    public Guid EntryId { get; set; }

    public string NewName { get; set; } = "";
}
