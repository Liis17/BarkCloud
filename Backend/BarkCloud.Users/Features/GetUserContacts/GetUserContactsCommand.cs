using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.GetUserContacts;

public class GetUserContactsCommand : IRequest<GetUserContactsResponse>
{
    public long UserId { get; set; }
}