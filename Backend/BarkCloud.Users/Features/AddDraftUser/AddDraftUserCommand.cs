using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.AddDraftUser;

public class AddDraftUserCommand : IRequest<AddDraftUserResponse>
{
    public string FirstName { get; set; }

    public string LastName { get; set; }

    public string Username { get; set; }

    public string Email { get; set; }
}