using BarkCloud.Proto.Files;

using MediatR;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Features.Cloud.GetUserMediaStats;

public sealed class GetUserMediaStatsCommand : IRequest<GetUserMediaStatsResponse>
{
    public DomainMediaKind Kind { get; init; }
}
