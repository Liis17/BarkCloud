using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyFolderShares;

public class ListMyFolderSharesCommand : IRequest<ListMyFolderSharesResponse>
{
    public int Limit { get; set; }

    public DateTime? CursorCreatedAt { get; set; }

    public Guid? CursorFolderShareId { get; set; }
}
