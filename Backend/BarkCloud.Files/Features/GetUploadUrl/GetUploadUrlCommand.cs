using BarkCloud.Proto.Files;

using MediatR;

using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Features.GetUploadUrl;

public class GetUploadUrlCommand : IRequest<GetUploadUrlResponse>
{
    public UploadFileType Type { get; set; }
}