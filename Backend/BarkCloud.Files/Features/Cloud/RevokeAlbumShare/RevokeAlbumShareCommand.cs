using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeAlbumShare;

public class RevokeAlbumShareCommand : IRequest<CloudEmpty>
{
    public Guid AlbumShareId { get; set; }
}
