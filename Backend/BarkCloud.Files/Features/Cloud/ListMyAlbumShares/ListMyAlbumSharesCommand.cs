using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyAlbumShares;

public class ListMyAlbumSharesCommand : IRequest<ListMyAlbumSharesResponse>
{
    public int Limit { get; set; }

    public DateTime? CursorCreatedAt { get; set; }

    public Guid? CursorAlbumShareId { get; set; }
}
