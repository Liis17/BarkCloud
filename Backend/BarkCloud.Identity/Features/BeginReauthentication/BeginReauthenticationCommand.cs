using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.BeginReauthentication;

public sealed class BeginReauthenticationCommand : IRequest<BeginReauthenticationResponse>
{
    public ReauthenticationAction Action { get; set; }
    public string CredentialId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string OtpCode { get; set; } = string.Empty;
    public string ChallengeId { get; set; } = string.Empty;
}
