using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.SetVideoThumbnail;

public class SetVideoThumbnailCommand : IRequest<CloudEmpty>
{
    public Guid VideoFileId { get; set; }

    public Guid SourceImageFileId { get; set; }
}
