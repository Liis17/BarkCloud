using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.CreateAlbumShare;

public class CreateAlbumShareCommand : IRequest<AlbumShareInfo>
{
    public Guid AlbumId { get; set; }

    public string Name { get; set; } = string.Empty;
}
