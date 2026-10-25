using System.Security.Claims;

using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.CompleteReauthentication;
using BarkCloud.Identity.Features.CompleteWebAuthnRegistration;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;

using Fido2NetLib;
using Fido2NetLib.Objects;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.IntegrationTests;

public sealed class PasskeyReauthenticationAtomicityTests
{
    private const long UserId = 42;
    private const string PermissionToken = "registration-permission-token";
    private static readonly byte[] NewCredentialId = [4, 5, 6];

    [Fact]
    public async Task Registration_DoubleCompletion_HasOneWinner()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var (challengeId, sessionId) = await SeedRegistration(database);
        var proofBarrier = new ProofBarrier(2);

        async Task<bool> Complete()
        {
            await using var context = database.CreateContext();
            try
            {
                await CreateHandler(context, sessionId, proofBarrier.Fido.Object).Handle(
                    Request(challengeId), default);
                return true;
            }
            catch (ReauthenticationRequiredException)
            {
                return false;
            }
        }

        var first = Complete();
        var second = Complete();
        await proofBarrier.Reached;
        proofBarrier.Release();

        (await Task.WhenAll(first, second)).Count(x => x).Should().Be(1);
        await using var reader = database.CreateContext();
        (await reader.WebAuthnCredentials.AsNoTracking().CountAsync(x => x.UserId == UserId)).Should().Be(1);
        (await reader.WebAuthnChallenges.AnyAsync(x => x.Id == challengeId)).Should().BeFalse();
        (await reader.ReauthPermissions.AsNoTracking().SingleAsync()).Status
            .Should().Be(ReauthPermissionStatus.Consumed);
    }

    [Fact]
    public async Task PasswordReset_WhileRegistrationProofIsPending_InvalidatesRegistration()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var reset = await ResetPasswordTests.Seed(setup);
        var sessionId = (await new RefreshTokensStorage(setup, new JwtSettings()).FindRefreshToken("old-current"))!.Id;
        var challengeId = await AddRegistration(setup, sessionId);

        var proofStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fido = CreateFido(async () =>
        {
            proofStarted.TrySetResult();
            await releaseProof.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        await using var registrationContext = database.CreateContext();
        var registration = CreateHandler(registrationContext, sessionId, fido.Object)
            .Handle(Request(challengeId), default);
        await proofStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var resetContext = database.CreateContext();
        await ResetPasswordTests.CreateHandler(resetContext)
            .Handle(ResetPasswordTests.Command(reset.Id), default);
        releaseProof.TrySetResult();

        await FluentActions.Awaiting(() => registration)
            .Should().ThrowAsync<ReauthenticationRequiredException>();
        await using var reader = database.CreateContext();
        (await reader.WebAuthnChallenges.AnyAsync(x => x.Id == challengeId)).Should().BeFalse();
        (await reader.WebAuthnCredentials.AnyAsync(x => x.UserId == UserId)).Should().BeFalse();
        (await new PasswordsStorage(reader).GetSecurityStateVersion(UserId)).Should().Be(2);
    }

    [Fact]
    public async Task PasskeyReauthentication_ZeroSignatureCounterCompletesPermission()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var challengeId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        var assertionCredentialId = new byte[] { 1, 2, 3 };
        long sessionId;
        await using (var setup = database.CreateContext())
        {
            await new PasswordsStorage(setup).UpdateUserPasswordHash(UserId, "password-hash");
            sessionId = (await new RefreshTokensStorage(setup, new JwtSettings())
                .CreateNewRefreshToken("active-session", UserId, "phone", 9999))!.Id;
            var expiresAt = DateTime.UtcNow.AddMinutes(5);
            var options = CreateFido().GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = [new PublicKeyCredentialDescriptor(assertionCredentialId)],
                UserVerification = UserVerificationRequirement.Required
            });
            setup.WebAuthnCredentials.Add(new WebAuthnCredential
            {
                UserId = UserId,
                CredentialId = assertionCredentialId,
                PublicKey = [7, 8, 9],
                SignatureCounter = 0,
                Name = "zero counter key",
                CreatedAt = DateTime.UtcNow
            });
            setup.ReauthPermissions.Add(new ReauthPermission
            {
                Id = permissionId,
                UserId = UserId,
                SessionId = sessionId,
                Action = (int)ReauthenticationAction.AddPasskey,
                SecurityStateVersion = 1,
                Status = ReauthPermissionStatus.PasskeyPending,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                WebAuthnChallengeId = challengeId
            });
            setup.WebAuthnChallenges.Add(new WebAuthnChallenge
            {
                Id = challengeId,
                UserId = UserId,
                Type = WebAuthnChallengeType.ReauthenticationAssertion,
                OptionsJson = options.ToJson(),
                ExpiresAt = expiresAt,
                SessionId = sessionId,
                SecurityStateVersion = 1,
                ReauthPermissionId = permissionId
            });
            await setup.SaveChangesAsync();
        }

        var fido = new Mock<IFido2>();
        fido.Setup(x => x.MakeAssertionAsync(It.IsAny<MakeAssertionParams>(), It.IsAny<CancellationToken>()))
            .Returns((MakeAssertionParams parameters, CancellationToken _) =>
            {
                parameters.StoredSignatureCounter.Should().Be(0);
                parameters.OriginalOptions.UserVerification.Should().Be(UserVerificationRequirement.Required);
                return Task.FromResult(new VerifyAssertionResult { SignCount = 0 });
            });
        await using var context = database.CreateContext();
        var userContext = CreateUserContext(sessionId);
        var passwordsStorage = new PasswordsStorage(context);
        var handler = new CompleteReauthenticationCommandHandler(userContext, context,
            new AuthPropertiesStorage(context), passwordsStorage, new WebAuthnStorage(context),
            new ReauthenticationService(context, passwordsStorage), fido.Object,
            NullLogger<CompleteReauthenticationCommandHandler>.Instance);

        var response = await handler.Handle(new CompleteReauthenticationCommand
        {
            ChallengeId = challengeId.ToString(),
            AssertionJson = "{\"id\":\"AQID\",\"rawId\":\"AQID\"}"
        }, default);

        await using var reader = database.CreateContext();
        var credential = await reader.WebAuthnCredentials.AsNoTracking().SingleAsync();
        credential.SignatureCounter.Should().Be(0);
        credential.LastUsedAt.Should().NotBeNull();
        (await reader.WebAuthnChallenges.AnyAsync(x => x.Id == challengeId)).Should().BeFalse();
        var permission = await reader.ReauthPermissions.AsNoTracking().SingleAsync();
        permission.Status.Should().Be(ReauthPermissionStatus.Granted);
        permission.PermissionTokenHash.Should().Equal(ReauthenticationService.HashToken(response.PermissionToken));
    }

    private static async Task<(Guid ChallengeId, long SessionId)> SeedRegistration(PostgresIdentityDatabase database)
    {
        await using var context = database.CreateContext();
        await new PasswordsStorage(context).UpdateUserPasswordHash(UserId, "password-hash");
        var session = await new RefreshTokensStorage(context, new JwtSettings())
            .CreateNewRefreshToken("active-session", UserId, "phone", 9999);
        var sessionId = session!.Id;
        var challengeId = await AddRegistration(context, sessionId);
        return (challengeId, sessionId);
    }

    private static async Task<Guid> AddRegistration(IdentityContext context, long sessionId)
    {
        const long version = 1;
        var challengeId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.AddMinutes(5);
        var options = CreateCredentialOptions();
        context.ReauthPermissions.Add(new ReauthPermission
        {
            Id = permissionId,
            UserId = UserId,
            SessionId = sessionId,
            Action = (int)ReauthenticationAction.AddPasskey,
            SecurityStateVersion = version,
            PermissionTokenHash = ReauthenticationService.HashToken(PermissionToken),
            Status = ReauthPermissionStatus.RegistrationPending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            WebAuthnChallengeId = challengeId
        });
        context.WebAuthnChallenges.Add(new WebAuthnChallenge
        {
            Id = challengeId,
            UserId = UserId,
            Type = WebAuthnChallengeType.Registration,
            OptionsJson = options.ToJson(),
            ExpiresAt = expiresAt,
            SessionId = sessionId,
            SecurityStateVersion = version,
            ReauthPermissionId = permissionId
        });
        await context.SaveChangesAsync();
        return challengeId;
    }

    private static CompleteWebAuthnRegistrationCommand Request(Guid challengeId) => new()
    {
        ChallengeId = challengeId.ToString(),
        AttestationJson = "{}",
        CredentialName = "test key",
        PermissionToken = PermissionToken
    };

    private static CompleteWebAuthnRegistrationCommandHandler CreateHandler(IdentityContext context,
        long sessionId, IFido2 fido)
    {
        return new CompleteWebAuthnRegistrationCommandHandler(CreateUserContext(sessionId), context,
            new PasswordsStorage(context), new WebAuthnStorage(context),
            new ReauthenticationService(context, new PasswordsStorage(context)), fido,
            new GrpcServer.Metrics.MetricsCollector(),
            NullLogger<CompleteWebAuthnRegistrationCommandHandler>.Instance);
    }

    private static UserContext CreateUserContext(long sessionId)
    {
        var claims = new[]
        {
            new Claim(IdentityClaims.UserId, UserId.ToString()),
            new Claim(IdentityClaims.TokenType, TokenType.User.ToString()),
            new Claim(IdentityClaims.SessionId, sessionId.ToString())
        };
        return new UserContext(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "integration-test"))
            }
        });
    }

    private static Fido2 CreateFido()
        => new(new Fido2Configuration
        {
            ServerDomain = "localhost",
            ServerName = "Identity integration tests",
            Origins = new HashSet<string> { "https://localhost" }
        });

    private static CredentialCreateOptions CreateCredentialOptions()
        => new Fido2(new Fido2Configuration
        {
            ServerDomain = "localhost",
            ServerName = "Identity integration tests",
            Origins = new HashSet<string> { "https://localhost" }
        }).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = [1, 2, 3], Name = "user", DisplayName = "User" },
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required
            }
        });

    private static Mock<IFido2> CreateFido(Func<Task>? beforeReturn = null)
    {
        var fido = new Mock<IFido2>();
        fido.Setup(x => x.MakeNewCredentialAsync(It.IsAny<MakeNewCredentialParams>(), It.IsAny<CancellationToken>()))
            .Returns(async (MakeNewCredentialParams _, CancellationToken _) =>
            {
                if (beforeReturn is not null)
                    await beforeReturn();
                return new RegisteredPublicKeyCredential
                {
                    Id = NewCredentialId,
                    PublicKey = [7, 8, 9],
                    SignCount = 0,
                    AaGuid = Guid.Empty,
                    AttestationFormat = "none"
                };
            });
        return fido;
    }

    private sealed class ProofBarrier
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        public Mock<IFido2> Fido { get; }

        public ProofBarrier(int participants)
        {
            Fido = new Mock<IFido2>();
            Fido.Setup(x => x.MakeNewCredentialAsync(It.IsAny<MakeNewCredentialParams>(), It.IsAny<CancellationToken>()))
                .Returns(async (MakeNewCredentialParams _, CancellationToken _) =>
                {
                    if (Interlocked.Increment(ref _arrivals) == participants)
                        _reached.TrySetResult();
                    await _release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    return new RegisteredPublicKeyCredential
                    {
                        Id = NewCredentialId, PublicKey = [7, 8, 9], SignCount = 0,
                        AaGuid = Guid.Empty, AttestationFormat = "none"
                    };
                });
        }

        public void Release() => _release.TrySetResult();
    }
}
