using BarkCloud.Proto.Files;

using MediatR;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Features.Album.ListAlbumItems;

public class ListAlbumItemsCommand : IRequest<ListAlbumItemsResponse>
{
    public Guid AlbumId { get; set; }

    public int Limit { get; set; }

    public DateTime? CursorAddedAt { get; set; }

    public Guid? CursorFileId { get; set; }

    /// <summary>null = элементы всех типов.</summary>
    public DomainMediaKind? KindFilter { get; set; }
}
