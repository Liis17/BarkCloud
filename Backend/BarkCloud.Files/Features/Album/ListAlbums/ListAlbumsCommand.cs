using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Album.ListAlbums;

public class ListAlbumsCommand : IRequest<ListAlbumsResponse>
{
    public int Limit { get; set; }

    public DateTime? CursorUpdatedAt { get; set; }

    public Guid? CursorAlbumId { get; set; }
}
