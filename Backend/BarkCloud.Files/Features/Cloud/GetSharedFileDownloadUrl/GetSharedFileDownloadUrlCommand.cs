using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.GetSharedFileDownloadUrl;

public class GetSharedFileDownloadUrlCommand : IRequest<GetSharedFileDownloadUrlResponse>
{
    public Guid FileId { get; set; }
}
