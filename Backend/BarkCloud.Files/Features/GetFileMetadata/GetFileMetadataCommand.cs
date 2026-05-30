using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetFileMetadata;

public class GetFileMetadataCommand : IRequest<GetFileMetadataResponse>
{
    public Guid FileId { get; set; }
}
