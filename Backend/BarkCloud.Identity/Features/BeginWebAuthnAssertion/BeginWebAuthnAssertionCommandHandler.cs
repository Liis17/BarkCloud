using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;

using Fido2NetLib;
using Fido2NetLib.Objects;

using MediatR;

namespace BarkCloud.Identity.Features.BeginWebAuthnAssertion;

public class BeginWebAuthnAssertionCommandHandler(
    IWebAuthnStorage webAuthnStorage,
    IFido2 fido2,
    ILogger<BeginWebAuthnAssertionCommandHandler> logger)
    : IRequestHandler<BeginWebAuthnAssertionCommand, BeginWebAuthnAssertionResponse>
{
    public async Task<BeginWebAuthnAssertionResponse> Handle(BeginWebAuthnAssertionCommand request, CancellationToken cancellationToken)
    {
        // Passwordless: пустой allowCredentials → клиент покажет выбор любого resident-ключа
        // этого RP. Пользователь определяется на complete по user handle из assertion.
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = new List<PublicKeyCredentialDescriptor>(),
            UserVerification = UserVerificationRequirement.Required
        });

        var challengeId = Guid.NewGuid();
        await webAuthnStorage.SaveChallenge(new Domain.WebAuthnChallenge
        {
            Id = challengeId,
            UserId = 0, // неизвестен до завершения (discoverable)
            Type = Domain.WebAuthnChallengeType.Assertion,
            OptionsJson = options.ToJson(),
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        logger.LogInformation("Начало passwordless-входа по ключу (challenge {ChallengeId})", challengeId);

        return new BeginWebAuthnAssertionResponse
        {
            OptionsJson = options.ToJson(),
            ChallengeId = challengeId.ToString()
        };
    }
}
