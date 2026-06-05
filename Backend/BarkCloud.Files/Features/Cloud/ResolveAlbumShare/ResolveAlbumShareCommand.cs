using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ResolveAlbumShare;

public class ResolveAlbumShareCommand : IRequest<ResolveAlbumShareResponse>
{
    public string Token { get; set; } = string.Empty;

    public int Limit { get; set; }

    public DateTime? CursorAddedAt { get; set; }

    public Guid? CursorFileId { get; set; }
}
