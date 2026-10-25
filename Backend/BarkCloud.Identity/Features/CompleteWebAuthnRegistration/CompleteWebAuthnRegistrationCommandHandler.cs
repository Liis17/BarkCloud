using System.Text.Json;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;

using Fido2NetLib;
using Fido2NetLib.Objects;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Features.CompleteWebAuthnRegistration;

public class CompleteWebAuthnRegistrationCommandHandler(
    UserContext userContext,
    IdentityContext context,
    IPasswordsStorage passwordsStorage,
    IWebAuthnStorage webAuthnStorage,
    ReauthenticationService reauthenticationService,
    IFido2 fido2,
    MetricsCollector metrics,
    ILogger<CompleteWebAuthnRegistrationCommandHandler> logger)
    : IRequestHandler<CompleteWebAuthnRegistrationCommand, CompleteWebAuthnRegistrationResponse>
{
    public async Task<CompleteWebAuthnRegistrationResponse> Handle(CompleteWebAuthnRegistrationCommand request,
        CancellationToken cancellationToken)
    {
        var userId = userContext.UserId;
        if (!Guid.TryParse(request.ChallengeId, out var challengeId)
            || userContext.SessionId is not { } sessionId)
            throw new WebAuthnChallengeExpiredException();

        var challenge = await webAuthnStorage.GetChallenge(challengeId, cancellationToken);
        if (challenge is null || challenge.UserId != userId || challenge.Type != WebAuthnChallengeType.Registration
            || challenge.SessionId != sessionId || challenge.ReauthPermissionId is not { } permissionId
            || challenge.ExpiresAt <= DateTime.UtcNow
            || challenge.SecurityStateVersion != await passwordsStorage.GetSecurityStateVersion(userId, cancellationToken))
            throw new WebAuthnChallengeExpiredException();

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
            throw new WebAuthnVerificationFailedException();

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

        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await IdentityOwnerLock.AcquireAsync(context, userId, cancellationToken);
        var currentChallenge = await webAuthnStorage.GetChallenge(challengeId, cancellationToken);
        var securityVersion = await passwordsStorage.GetSecurityStateVersion(userId, cancellationToken);
        if (currentChallenge is null || currentChallenge.UserId != userId
            || currentChallenge.Type != WebAuthnChallengeType.Registration
            || currentChallenge.SessionId != sessionId || currentChallenge.ExpiresAt <= DateTime.UtcNow
            || currentChallenge.SecurityStateVersion != securityVersion
            || currentChallenge.ReauthPermissionId != permissionId
            || !await reauthenticationService.TryCompleteRegistrationAsync(request.PermissionToken, permissionId,
                challengeId, userId, sessionId, securityVersion, cancellationToken))
            throw new ReauthenticationRequiredException();

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
        await transaction.CommitAsync(cancellationToken);

        metrics.Increment("webauthn_credentials_registered");
        logger.LogInformation("Ключ безопасности привязан к пользователю {UserId}", userId);
        return new CompleteWebAuthnRegistrationResponse();
    }
}
