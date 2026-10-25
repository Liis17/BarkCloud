using System.Text.Json;

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

namespace BarkCloud.Identity.Features.CompleteReauthentication;

public sealed class CompleteReauthenticationCommandHandler(
    UserContext userContext,
    IdentityContext context,
    IAuthPropertiesStorage authPropertiesStorage,
    IPasswordsStorage passwordsStorage,
    IWebAuthnStorage webAuthnStorage,
    ReauthenticationService reauthenticationService,
    IFido2 fido2,
    ILogger<CompleteReauthenticationCommandHandler> logger)
    : IRequestHandler<CompleteReauthenticationCommand, CompleteReauthenticationResponse>
{
    public async Task<CompleteReauthenticationResponse> Handle(CompleteReauthenticationCommand request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.ChallengeId, out var challengeId)
            || userContext.SessionId is not { } sessionId)
            throw new ReauthenticationRequiredException();

        var emailPermission = await context.ReauthPermissions.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Id == challengeId && x.UserId == userContext.UserId && x.SessionId == sessionId
            && x.Status == ReauthPermissionStatus.EmailPending && x.ExpiresAt > DateTime.UtcNow,
            cancellationToken);
        if (emailPermission is not null)
            return await CompleteEmailAsync(emailPermission, request.EmailCode, sessionId, cancellationToken);

        return await CompletePasskeyAsync(challengeId, request.AssertionJson, sessionId, cancellationToken);
    }

    private async Task<CompleteReauthenticationResponse> CompleteEmailAsync(ReauthPermission permission,
        string code, long sessionId, CancellationToken cancellationToken)
    {
        var validatedCode = await authPropertiesStorage.TryValidateAndReserveEmailAuthCode(permission.UserId,
            EmailAuthCodePurpose.Reauthentication, code, cancellationToken, permission.Id);
        if (validatedCode is null)
            throw new NotValidOtpCodeException();

        var (token, hash) = CreateToken();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await IdentityOwnerLock.AcquireAsync(context, permission.UserId, cancellationToken);
        var current = await context.ReauthPermissions.FirstOrDefaultAsync(x => x.Id == permission.Id
            && x.UserId == permission.UserId && x.SessionId == sessionId
            && x.Status == ReauthPermissionStatus.EmailPending && x.ExpiresAt > DateTime.UtcNow,
            cancellationToken);
        if (current is null || current.SecurityStateVersion
            != await passwordsStorage.GetSecurityStateVersion(permission.UserId, cancellationToken)
            || !await reauthenticationService.IsSessionActive(permission.UserId, sessionId, cancellationToken))
            throw new ReauthenticationRequiredException();
        if (await authPropertiesStorage.GetUserAuthProperties(permission.UserId, cancellationToken)
            is { OtpEnabled: true })
            throw new OtpCodeNeedException("authenticator");
        if (!await authPropertiesStorage.TryConsumeValidatedEmailAuthCode(validatedCode, cancellationToken))
            throw new ReauthenticationRequiredException();

        current.PermissionTokenHash = hash;
        current.Status = ReauthPermissionStatus.Granted;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CompleteReauthenticationResponse { PermissionToken = token };
    }

    private async Task<CompleteReauthenticationResponse> CompletePasskeyAsync(Guid challengeId, string assertionJson,
        long sessionId, CancellationToken cancellationToken)
    {
        var challenge = await webAuthnStorage.GetChallenge(challengeId, cancellationToken);
        if (challenge is null || challenge.Type != WebAuthnChallengeType.ReauthenticationAssertion
            || challenge.UserId != userContext.UserId || challenge.SessionId != sessionId
            || challenge.ExpiresAt <= DateTime.UtcNow || challenge.ReauthPermissionId is null)
            throw new WebAuthnChallengeExpiredException();

        var permission = await context.ReauthPermissions.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Id == challenge.ReauthPermissionId && x.UserId == userContext.UserId && x.SessionId == sessionId
            && x.WebAuthnChallengeId == challenge.Id && x.Status == ReauthPermissionStatus.PasskeyPending
            && x.ExpiresAt > DateTime.UtcNow, cancellationToken);
        if (permission is null)
            throw new WebAuthnChallengeExpiredException();

        var options = AssertionOptions.FromJson(challenge.OptionsJson);
        AuthenticatorAssertionRawResponse? assertion;
        try
        {
            assertion = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson);
        }
        catch (JsonException)
        {
            throw new WebAuthnVerificationFailedException();
        }
        if (assertion is null)
            throw new WebAuthnVerificationFailedException();

        var credential = await webAuthnStorage.GetCredentialByCredentialId(assertion.RawId, cancellationToken);
        if (credential is null || credential.UserId != userContext.UserId)
            throw new WebAuthnVerificationFailedException();

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
                    var ownerId = await webAuthnStorage.GetUserIdByUserHandle(args.UserHandle, ct);
                    var ownerCredential = await webAuthnStorage.GetCredentialByCredentialId(args.CredentialId, ct);
                    return ownerId == userContext.UserId && ownerCredential?.UserId == userContext.UserId;
                }
            }, cancellationToken);
        }
        catch (Fido2VerificationException ex)
        {
            logger.LogWarning(ex, "Не удалось проверить reauth assertion ключа для пользователя {UserId}", userContext.UserId);
            throw new WebAuthnVerificationFailedException();
        }

        var (token, hash) = CreateToken();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await IdentityOwnerLock.AcquireAsync(context, userContext.UserId, cancellationToken);
        var current = await context.ReauthPermissions.FirstOrDefaultAsync(x => x.Id == permission.Id
            && x.UserId == userContext.UserId && x.SessionId == sessionId
            && x.Status == ReauthPermissionStatus.PasskeyPending && x.WebAuthnChallengeId == challenge.Id
            && x.ExpiresAt > DateTime.UtcNow, cancellationToken);
        if (current is null || current.SecurityStateVersion != challenge.SecurityStateVersion
            || await passwordsStorage.GetSecurityStateVersion(userContext.UserId, cancellationToken)
                != challenge.SecurityStateVersion
            || !await reauthenticationService.IsSessionActive(userContext.UserId, sessionId, cancellationToken))
            throw new ReauthenticationRequiredException();

        var consumed = await context.WebAuthnChallenges.Where(x => x.Id == challenge.Id
            && x.UserId == userContext.UserId && x.Type == WebAuthnChallengeType.ReauthenticationAssertion
            && x.SessionId == sessionId && x.SecurityStateVersion == challenge.SecurityStateVersion
            && x.ExpiresAt > DateTime.UtcNow).ExecuteDeleteAsync(cancellationToken);
        if (consumed != 1 || !await webAuthnStorage.TryUpdateCounter(credential.Id,
                credential.SignatureCounter, result.SignCount, cancellationToken))
            throw new WebAuthnVerificationFailedException();

        current.PermissionTokenHash = hash;
        current.Status = ReauthPermissionStatus.Granted;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CompleteReauthenticationResponse { PermissionToken = token };
    }

    private static (string Token, byte[] Hash) CreateToken()
    {
        var token = ReauthenticationService.CreatePermissionToken(out var hash);
        return (token, hash);
    }
}
