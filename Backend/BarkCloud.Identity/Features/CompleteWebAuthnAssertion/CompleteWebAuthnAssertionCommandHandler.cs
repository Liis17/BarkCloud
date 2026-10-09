using System.Text.Json;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;

using Fido2NetLib;
using Fido2NetLib.Objects;

using MediatR;

namespace BarkCloud.Identity.Features.CompleteWebAuthnAssertion;

public class CompleteWebAuthnAssertionCommandHandler(
    IWebAuthnStorage webAuthnStorage,
    IFido2 fido2,
    SessionIssuer sessionIssuer,
    IAuthRateLimiter rateLimiter,
    MetricsCollector metrics,
    ILogger<CompleteWebAuthnAssertionCommandHandler> logger)
    : IRequestHandler<CompleteWebAuthnAssertionCommand, AuthResponse>
{
    public async Task<AuthResponse> Handle(CompleteWebAuthnAssertionCommand request, CancellationToken cancellationToken)
    {
        await rateLimiter.EnsureSourceAsync(AuthLimits.WebAuthnByIp);

        if (!Guid.TryParse(request.ChallengeId, out var challengeId))
        {
            throw new WebAuthnChallengeExpiredException();
        }

        var challenge = await webAuthnStorage.GetChallenge(challengeId, cancellationToken);

        if (challenge is null || challenge.Type != WebAuthnChallengeType.Assertion)
        {
            throw new WebAuthnChallengeExpiredException();
        }

        if (challenge.ExpiresAt < DateTime.UtcNow)
        {
            await webAuthnStorage.DeleteChallenge(challengeId);
            throw new WebAuthnChallengeExpiredException();
        }

        var options = AssertionOptions.FromJson(challenge.OptionsJson);

        AuthenticatorAssertionRawResponse? assertion;
        try
        {
            assertion = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(request.AssertionJson);
        }
        catch (JsonException)
        {
            throw new WebAuthnVerificationFailedException();
        }

        if (assertion is null)
        {
            throw new WebAuthnVerificationFailedException();
        }

        // Passwordless: пользователь определяется самим ключом (challenge не привязан к userId).
        var credential = await webAuthnStorage.GetCredentialByCredentialId(assertion.RawId, cancellationToken);
        if (credential is null)
        {
            throw new WebAuthnVerificationFailedException();
        }

        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertion,
                OriginalOptions = options,
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = (uint)credential.SignatureCounter,
                IsUserHandleOwnerOfCredentialIdCallback = async (args, ct) =>
                {
                    var ownerUserId = await webAuthnStorage.GetUserIdByUserHandle(args.UserHandle, ct);
                    if (ownerUserId is null)
                    {
                        return false;
                    }

                    var owned = await webAuthnStorage.GetCredentialByCredentialId(args.CredentialId, ct);
                    return owned is not null && owned.UserId == ownerUserId;
                }
            }, cancellationToken);
        }
        catch (Fido2VerificationException ex)
        {
            logger.LogWarning(ex, "Не удалось проверить assertion ключа (challenge {ChallengeId})", challengeId);
            throw new WebAuthnVerificationFailedException();
        }

        var response = await sessionIssuer.IssueAsync(credential.UserId, cancellationToken, async transactionToken =>
        {
            if (!await webAuthnStorage.TryConsumeAssertionChallenge(challengeId, challenge.ExpiresAt, transactionToken))
            {
                throw new WebAuthnChallengeExpiredException();
            }

            if (!await webAuthnStorage.TryUpdateCounter(credential.Id, credential.SignatureCounter,
                    result.SignCount, transactionToken))
            {
                throw new WebAuthnVerificationFailedException();
            }
        });

        metrics.Increment("webauthn_login_success");
        logger.LogInformation("Успешный вход по ключу для пользователя {UserId}", credential.UserId);

        return response;
    }
}
