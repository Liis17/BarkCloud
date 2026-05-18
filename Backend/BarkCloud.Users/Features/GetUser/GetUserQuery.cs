using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.GetUser;

public class GetUserQuery : IRequest<GetUserResponse>
{
    public long? UserId { get; init; }
}