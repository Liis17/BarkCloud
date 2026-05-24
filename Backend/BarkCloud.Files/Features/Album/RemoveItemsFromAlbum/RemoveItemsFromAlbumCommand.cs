using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Album.RemoveItemsFromAlbum;

public class RemoveItemsFromAlbumCommand : IRequest<CloudEmpty>
{
    public Guid AlbumId { get; set; }

    public List<Guid> FileIds { get; set; } = new();
}
