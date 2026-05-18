using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.CheckExistUsername;

public class CheckExistUsernameQuery : IRequest<CheckExistResponse>
{
    public string Username { get; set; }
}