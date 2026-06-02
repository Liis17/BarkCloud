using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyOutgoingSharesAll;

public class ListMyOutgoingSharesAllCommand : IRequest<ListMyOutgoingSharesAllResponse>
{
    public int Limit { get; set; }

    public DateTime? CursorSharedAt { get; set; }

    public Guid? CursorGrantId { get; set; }
}
