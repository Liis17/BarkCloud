using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.FindByLogin;

public class FindByLoginQuery : IRequest<FindByLoginResponse>
{
    public string? Username { get; set; }

    public string? Email { get; set; }
}