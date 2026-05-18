using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetFilesData;

public class GetFilesDataCommand : IRequest<GetFilesDataResponse>
{
    public List<Guid> FileIds { get; set; }
}