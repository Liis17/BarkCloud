using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.UpdateProfileServer;

public class UpdateProfileServerCommand : IRequest<UpdateProfileServerResponse>
{
    public long UserId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Username { get; set; }
}
