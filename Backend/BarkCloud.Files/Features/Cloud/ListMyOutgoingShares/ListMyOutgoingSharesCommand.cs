using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyOutgoingShares;

public class ListMyOutgoingSharesCommand : IRequest<ListMyOutgoingSharesResponse>
{
    public Guid FileId { get; set; }
}
