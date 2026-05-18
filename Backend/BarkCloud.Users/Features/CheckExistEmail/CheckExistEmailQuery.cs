using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.CheckExistEmail;

public class CheckExistEmailQuery : IRequest<CheckExistResponse>
{
    public string Email { get; set; }
}