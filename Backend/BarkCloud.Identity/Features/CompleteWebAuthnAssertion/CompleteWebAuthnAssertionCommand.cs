using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.CompleteWebAuthnAssertion;

public class CompleteWebAuthnAssertionCommand : IRequest<AuthResponse>
{
    public string ChallengeId { get; set; } = string.Empty;

    public string AssertionJson { get; set; } = string.Empty;
}
