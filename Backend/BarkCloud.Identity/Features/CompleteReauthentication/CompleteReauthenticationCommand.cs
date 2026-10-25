using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.CompleteReauthentication;

public sealed class CompleteReauthenticationCommand : IRequest<CompleteReauthenticationResponse>
{
    public string ChallengeId { get; set; } = string.Empty;
    public string AssertionJson { get; set; } = string.Empty;
    public string EmailCode { get; set; } = string.Empty;
}
