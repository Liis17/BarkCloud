using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;

using Fido2NetLib;
using Fido2NetLib.Objects;

using MediatR;

namespace BarkCloud.Identity.Features.BeginWebAuthnAssertion;

public class BeginWebAuthnAssertionCommandHandler(
    IWebAuthnStorage webAuthnStorage,
    IFido2 fido2,
    UsersServerApi.UsersServerApiClient usersClient,
    ILogger<BeginWebAuthnAssertionCommandHandler> logger)
    : IRequestHandler<BeginWebAuthnAssertionCommand, BeginWebAuthnAssertionResponse>
{
    public async Task<BeginWebAuthnAssertionResponse> Handle(BeginWebAuthnAssertionCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Username) && string.IsNullOrEmpty(request.Email))
        {
            throw new NoWebAuthnCredentialsException();
        }

        var usersRequest = new FindByLoginRequest();
        if (!string.IsNullOrEmpty(request.Username))
        {
            usersRequest.Username = request.Username;
        }
        else
        {
            usersRequest.Email = request.Email;
        }

        var user = await usersClient.FindByLoginAsync(usersRequest);

        // Анти-enumeration: отсутствие пользователя и отсутствие ключей дают одну ошибку.
        if (user.User is null)
        {
            throw new NoWebAuthnCredentialsException();
        }

        var creds = await webAuthnStorage.GetCredentialsByUserId(user.User.Id);
        if (creds.Count == 0)
        {
            throw new NoWebAuthnCredentialsException();
        }

        var allowed = creds
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToList();

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            UserVerification = UserVerificationRequirement.Preferred
        });

        var challengeId = Guid.NewGuid();
        await webAuthnStorage.SaveChallenge(new Domain.WebAuthnChallenge
        {
            Id = challengeId,
            UserId = user.User.Id,
            Type = Domain.WebAuthnChallengeType.Assertion,
            OptionsJson = options.ToJson(),
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        logger.LogInformation("Начало входа по ключу для пользователя {UserId}", user.User.Id);

        return new BeginWebAuthnAssertionResponse
        {
            OptionsJson = options.ToJson(),
            ChallengeId = challengeId.ToString()
        };
    }
}
