using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;

using Fido2NetLib;
using Fido2NetLib.Objects;

using MediatR;

namespace BarkCloud.Identity.Features.BeginWebAuthnRegistration;

public class BeginWebAuthnRegistrationCommandHandler(
    UserContext userContext,
    IWebAuthnStorage webAuthnStorage,
    IFido2 fido2,
    UsersServerApi.UsersServerApiClient usersClient,
    ILogger<BeginWebAuthnRegistrationCommandHandler> logger)
    : IRequestHandler<BeginWebAuthnRegistrationCommand, BeginWebAuthnRegistrationResponse>
{
    public async Task<BeginWebAuthnRegistrationResponse> Handle(BeginWebAuthnRegistrationCommand request, CancellationToken cancellationToken)
    {
        var userId = userContext.UserId;

        logger.LogInformation("Начало привязки ключа безопасности для пользователя {UserId}", userId);

        var userInfo = await usersClient.GetByIdAsync(new GetByIdRequest { UserId = userId });
        var userHandle = await webAuthnStorage.GetOrCreateUserHandle(userId);

        var existing = await webAuthnStorage.GetCredentialsByUserId(userId);
        var excludeCredentials = existing
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToList();

        var displayName = $"{userInfo.User.FirstName} {userInfo.User.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = userInfo.User.Username;
        }

        var fidoUser = new Fido2User
        {
            Id = userHandle,
            Name = userInfo.User.Username,
            DisplayName = displayName
        };

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fidoUser,
            ExcludeCredentials = excludeCredentials,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                // username-first: resident key не требуется
                ResidentKey = ResidentKeyRequirement.Discouraged,
                UserVerification = UserVerificationRequirement.Preferred
            },
            AttestationPreference = AttestationConveyancePreference.None,
            Extensions = new AuthenticationExtensionsClientInputs { CredProps = true }
        });

        var challengeId = Guid.NewGuid();
        await webAuthnStorage.SaveChallenge(new Domain.WebAuthnChallenge
        {
            Id = challengeId,
            UserId = userId,
            Type = Domain.WebAuthnChallengeType.Registration,
            OptionsJson = options.ToJson(),
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        return new BeginWebAuthnRegistrationResponse
        {
            OptionsJson = options.ToJson(),
            ChallengeId = challengeId.ToString()
        };
    }
}
