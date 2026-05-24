using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.Devices.SetFirebaseToken;

public class SetFirebaseTokenCommand : IRequest<SetFirebaseTokenResponse>
{
    public string? FirebaseToken { get; set; }
}
