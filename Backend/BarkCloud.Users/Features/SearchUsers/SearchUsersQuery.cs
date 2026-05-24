using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.SearchUsers;

public class SearchUsersQuery : IRequest<SearchUsersResponse>
{
    public string Query { get; set; }

    public int Limit { get; set; }
}
