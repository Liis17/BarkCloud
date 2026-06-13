using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.ListWebAuthnCredentials;

public class ListWebAuthnCredentialsCommand : IRequest<ListWebAuthnCredentialsResponse>
{
}
