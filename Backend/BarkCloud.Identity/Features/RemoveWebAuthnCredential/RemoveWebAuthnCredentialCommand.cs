using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.RemoveWebAuthnCredential;

public class RemoveWebAuthnCredentialCommand : IRequest<RemoveWebAuthnCredentialResponse>
{
    public string CredentialId { get; set; } = string.Empty;

    public string PermissionToken { get; set; } = string.Empty;
}
