using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.CreateToken;

public class CreateTokenCommand : IRequest<CreateTokenResponse>
{
    public string RefreshToken { get; set; }
}