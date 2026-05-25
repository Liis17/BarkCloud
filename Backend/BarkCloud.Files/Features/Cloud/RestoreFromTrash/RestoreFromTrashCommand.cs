using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RestoreFromTrash;

public class RestoreFromTrashCommand : IRequest<CloudEmpty>
{
    public Guid EntryId { get; set; }
}
