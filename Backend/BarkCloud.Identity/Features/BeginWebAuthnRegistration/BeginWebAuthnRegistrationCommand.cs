using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.BeginWebAuthnRegistration;

public class BeginWebAuthnRegistrationCommand : IRequest<BeginWebAuthnRegistrationResponse>
{
}
