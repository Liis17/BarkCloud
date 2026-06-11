using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListFileActivity;

public class ListFileActivityCommand : IRequest<ListFileActivityResponse>
{
    public Guid FileId { get; set; }

    public int Limit { get; set; }

    public DateTime? CursorCreatedAt { get; set; }

    public Guid? CursorEventId { get; set; }
}
