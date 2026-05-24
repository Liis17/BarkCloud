using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Album.DeleteAlbum;

public class DeleteAlbumCommand : IRequest<CloudEmpty>
{
    public Guid AlbumId { get; set; }
}
