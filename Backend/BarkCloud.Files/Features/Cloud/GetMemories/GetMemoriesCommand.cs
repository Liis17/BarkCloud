using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.GetMemories;

public class GetMemoriesCommand : IRequest<GetMemoriesResponse>
{
    /// <summary>Месяц 1..12; 0 = текущий месяц (UTC).</summary>
    public int Month { get; set; }

    /// <summary>День 1..31; 0 = текущий день (UTC).</summary>
    public int Day { get; set; }

    /// <summary>Максимум превью на год; 0 = default.</summary>
    public int PerYearLimit { get; set; }
}
