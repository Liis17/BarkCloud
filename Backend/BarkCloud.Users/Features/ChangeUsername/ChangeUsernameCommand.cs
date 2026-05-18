using MediatR;

namespace BarkCloud.Users.Features.ChangeUsername;

public class ChangeUsernameCommand : IRequest
{
    public string Username { get; set; }
}