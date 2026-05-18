using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetFileData;

public class GetFileDataCommand : IRequest<GetFileDataResponse>
{
    public Guid FileId { get; set; }
}