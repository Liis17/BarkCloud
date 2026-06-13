using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.BeginWebAuthnAssertion;

public class BeginWebAuthnAssertionCommand : IRequest<BeginWebAuthnAssertionResponse>
{
    // Логин не нужен: passwordless discoverable-вход.
}
