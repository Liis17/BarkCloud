using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.CompleteWebAuthnRegistration;

public class CompleteWebAuthnRegistrationCommand : IRequest<CompleteWebAuthnRegistrationResponse>
{
    public string ChallengeId { get; set; } = string.Empty;

    public string AttestationJson { get; set; } = string.Empty;

    public string CredentialName { get; set; } = string.Empty;
}
