using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Album.AddItemsToAlbum;

public class AddItemsToAlbumCommand : IRequest<CloudEmpty>
{
    public Guid AlbumId { get; set; }

    public List<Guid> FileIds { get; set; } = new();
}
