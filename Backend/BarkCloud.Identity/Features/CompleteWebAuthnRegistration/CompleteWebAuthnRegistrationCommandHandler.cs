using System.Text.Json;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;

using Fido2NetLib;
using Fido2NetLib.Objects;

using MediatR;

namespace BarkCloud.Identity.Features.CompleteWebAuthnRegistration;

public class CompleteWebAuthnRegistrationCommandHandler(
    UserContext userContext,
    IWebAuthnStorage webAuthnStorage,
    IFido2 fido2,
    MetricsCollector metrics,
    ILogger<CompleteWebAuthnRegistrationCommandHandler> logger)
    : IRequestHandler<CompleteWebAuthnRegistrationCommand, CompleteWebAuthnRegistrationResponse>
{
    public async Task<CompleteWebAuthnRegistrationResponse> Handle(CompleteWebAuthnRegistrationCommand request, CancellationToken cancellationToken)
    {
        var userId = userContext.UserId;

        if (!Guid.TryParse(request.ChallengeId, out var challengeId))
        {
            throw new WebAuthnChallengeExpiredException();
        }

        var challenge = await webAuthnStorage.GetChallenge(challengeId);

        if (challenge is null || challenge.UserId != userId || challenge.Type != WebAuthnChallengeType.Registration)
        {
            throw new WebAuthnChallengeExpiredException();
        }

        if (challenge.ExpiresAt < DateTime.UtcNow)
        {
            await webAuthnStorage.DeleteChallenge(challengeId);
            throw new WebAuthnChallengeExpiredException();
        }

        var options = CredentialCreateOptions.FromJson(challenge.OptionsJson);

        AuthenticatorAttestationRawResponse? attestation;
        try
        {
            attestation = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(request.AttestationJson);
        }
        catch (JsonException)
        {
            throw new WebAuthnVerificationFailedException();
        }

        if (attestation is null)
        {
            throw new WebAuthnVerificationFailedException();
        }

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestation,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                    await webAuthnStorage.IsCredentialIdUnique(args.CredentialId)
            }, cancellationToken);
        }
        catch (Fido2VerificationException ex)
        {
            logger.LogWarning(ex, "Не удалось проверить attestation ключа для пользователя {UserId}", userId);
            throw new WebAuthnVerificationFailedException();
        }

        await webAuthnStorage.AddCredential(new WebAuthnCredential
        {
            UserId = userId,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignatureCounter = credential.SignCount,
            AaGuid = credential.AaGuid,
            CredType = credential.AttestationFormat,
            Name = string.IsNullOrWhiteSpace(request.CredentialName) ? "Ключ безопасности" : request.CredentialName.Trim(),
            CreatedAt = DateTime.UtcNow
        });

        await webAuthnStorage.DeleteChallenge(challengeId);

        metrics.Increment("webauthn_credentials_registered");

        logger.LogInformation("Ключ безопасности привязан к пользователю {UserId}", userId);

        return new CompleteWebAuthnRegistrationResponse();
    }
}
