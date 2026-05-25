using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.DeleteFromTrash;

public class DeleteFromTrashCommand : IRequest<CloudEmpty>
{
    public Guid EntryId { get; set; }
}
