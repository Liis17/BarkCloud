using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetTempDownloadUrl;

public class GetTempDownloadUrlCommand : IRequest<GetTempDownloadUrlResponse>
{
    public List<Guid> FileIds { get; set; }
}