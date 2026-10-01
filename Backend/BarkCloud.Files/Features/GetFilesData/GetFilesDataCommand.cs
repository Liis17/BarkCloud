using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetFilesData;

public class GetFilesDataCommand : IRequest<GetFilesDataResponse>
{
    public List<Guid> FileIds { get; set; }

    /// <summary>Для кого запрашиваем данные: 0 — отдаются только аватары.</summary>
    public long UserId { get; set; }
}