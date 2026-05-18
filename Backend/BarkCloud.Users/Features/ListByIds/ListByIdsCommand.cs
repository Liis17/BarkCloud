using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.ListByIds;

public class ListByIdsCommand : IRequest<ListByIdsResponse>
{
    public List<long> Ids { get; set; }
}