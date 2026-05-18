using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.DeleteFileEntry;

public class DeleteFileEntryCommand : IRequest<CloudEmpty>
{
    public Guid EntryId { get; set; }
}
