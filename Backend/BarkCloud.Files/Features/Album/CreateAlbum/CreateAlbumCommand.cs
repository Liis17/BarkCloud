using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Album.CreateAlbum;

public class CreateAlbumCommand : IRequest<AlbumInfo>
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";
}
