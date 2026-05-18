using MediatR;

namespace BarkCloud.Users.Features.ConfirmUser;

public class ConfirmUserCommand : IRequest
{
    public long UserId { get; set; }
}