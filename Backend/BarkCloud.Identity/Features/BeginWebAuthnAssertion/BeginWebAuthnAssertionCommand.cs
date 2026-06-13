using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.BeginWebAuthnAssertion;

public class BeginWebAuthnAssertionCommand : IRequest<BeginWebAuthnAssertionResponse>
{
    public string? Username { get; set; }

    public string? Email { get; set; }
}
