using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListSharedWithMe;

public class ListSharedWithMeCommand : IRequest<ListSharedWithMeResponse>
{
    public int Limit { get; set; }

    public DateTime? CursorSharedAt { get; set; }

    public Guid? CursorGrantId { get; set; }
}
