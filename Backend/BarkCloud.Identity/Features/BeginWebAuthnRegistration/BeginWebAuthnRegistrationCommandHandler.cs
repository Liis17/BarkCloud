using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;

using Fido2NetLib;
using Fido2NetLib.Objects;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Features.BeginWebAuthnRegistration;

public class BeginWebAuthnRegistrationCommandHandler(
    UserContext userContext,
    IdentityContext context,
    IPasswordsStorage passwordsStorage,
    IWebAuthnStorage webAuthnStorage,
    ReauthenticationService reauthenticationService,
    IFido2 fido2,
    UsersServerApi.UsersServerApiClient usersClient,
    ILogger<BeginWebAuthnRegistrationCommandHandler> logger)
    : IRequestHandler<BeginWebAuthnRegistrationCommand, BeginWebAuthnRegistrationResponse>
{
    public async Task<BeginWebAuthnRegistrationResponse> Handle(BeginWebAuthnRegistrationCommand request,
        CancellationToken cancellationToken)
    {
        var userId = userContext.UserId;
        if (userContext.SessionId is not { } sessionId)
            throw new ReauthenticationRequiredException();

        logger.LogInformation("Начало привязки ключа безопасности для пользователя {UserId}", userId);
        var userInfo = await usersClient.GetByIdAsync(new GetByIdRequest { UserId = userId },
            cancellationToken: cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await IdentityOwnerLock.AcquireAsync(context, userId, cancellationToken);
        var securityVersion = await passwordsStorage.GetSecurityStateVersion(userId, cancellationToken);
        if (!await reauthenticationService.IsSessionActive(userId, sessionId, cancellationToken))
            throw new ReauthenticationRequiredException();

        var userHandle = await webAuthnStorage.GetOrCreateUserHandle(userId);
        var existing = await webAuthnStorage.GetCredentialsByUserId(userId);
        var excludeCredentials = existing.Select(c => new PublicKeyCredentialDescriptor(c.CredentialId)).ToList();
        var displayName = $"{userInfo.User.FirstName} {userInfo.User.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = userInfo.User.Username;

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userHandle, Name = userInfo.User.Username, DisplayName = displayName },
            ExcludeCredentials = excludeCredentials,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required
            },
            AttestationPreference = AttestationConveyancePreference.None,
            Extensions = new AuthenticationExtensionsClientInputs { CredProps = true }
        });

        var challengeId = Guid.NewGuid();
        await context.ReauthPermissions.Where(x => x.ExpiresAt <= DateTime.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);
        await context.WebAuthnChallenges.Where(x => x.ExpiresAt <= DateTime.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);
        if (!await reauthenticationService.TryConsumeForRegistrationAsync(request.PermissionToken, userId,
                sessionId, (int)ReauthenticationAction.AddPasskey, securityVersion, challengeId, cancellationToken))
            throw new ReauthenticationRequiredException();

        await webAuthnStorage.SaveChallenge(new WebAuthnChallenge
        {
            Id = challengeId,
            UserId = userId,
            Type = WebAuthnChallengeType.Registration,
            OptionsJson = options.ToJson(),
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            SessionId = sessionId,
            SecurityStateVersion = securityVersion,
            ReauthPermissionId = await context.ReauthPermissions.Where(x => x.WebAuthnChallengeId == challengeId)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken)
        });
        await transaction.CommitAsync(cancellationToken);

        return new BeginWebAuthnRegistrationResponse
        {
            OptionsJson = options.ToJson(),
            ChallengeId = challengeId.ToString()
        };
    }
}
