using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetFileData;

public class GetFileDataCommand : IRequest<GetFileDataResponse>
{
    public Guid FileId { get; set; }

    /// <summary>Для кого запрашиваем данные: 0 — отдаются только аватары.</summary>
    public long UserId { get; set; }
}