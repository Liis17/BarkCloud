using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.CompleteWebAuthnAssertion;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using Fido2NetLib;
using Fido2NetLib.Objects;

using Grpc.Core;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.IntegrationTests;

public class WebAuthnAtomicityTests
{
    private static readonly byte[] CredentialId = [1, 2, 3];
    private const string OldRefresh = "old-refresh";

    [Fact]
    public async Task Assertion_WhenNotificationInsertFails_RestoresChallengeAndCounter()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var challengeId = Guid.NewGuid();
        var challengeExpiry = await SeedAssertion(database, challengeId);
        await using (var setup = database.CreateContext())
        {
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_notification_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'notification insert failed'; END $$;
                CREATE TRIGGER reject_notification_insert BEFORE INSERT ON "PendingNotifications"
                FOR EACH ROW EXECUTE FUNCTION reject_notification_insert();
                """);
        }

        await using var context = database.CreateContext();
        var metrics = new MetricsCollector();
        var handler = CreateHandler(context, metrics, new VerifyAssertionResult { SignCount = 9 });

        await FluentActions.Awaiting(() => handler.Handle(Request(challengeId), default))
            .Should().ThrowAsync<DbUpdateException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        (await observer.WebAuthnChallenges.AsNoTracking().AnyAsync(x => x.Id == challengeId
                && x.Type == WebAuthnChallengeType.Assertion && x.ExpiresAt == challengeExpiry)).Should().BeTrue();
        (await observer.WebAuthnCredentials.AsNoTracking().SingleAsync(x => x.CredentialId == CredentialId))
            .SignatureCounter.Should().Be(4);
        (await new RefreshTokensStorage(observer, Settings()).GetRefreshTokens(42)).Select(x => x.Value)
            .Should().Equal(OldRefresh);
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
        metrics.SnapshotAndReset().Should().NotContainKey("webauthn_login_success");
    }

    [Fact]
    public async Task Assertion_Success_CommitsChallengeCounterSessionAndEvent()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var challengeId = Guid.NewGuid();
        await SeedAssertion(database, challengeId);
        await using var context = database.CreateContext();
        var handler = CreateHandler(context, new MetricsCollector(), new VerifyAssertionResult { SignCount = 9 });

        var response = await handler.Handle(Request(challengeId), default);

        await using var observer = database.CreateContext();
        (await observer.WebAuthnChallenges.AnyAsync(x => x.Id == challengeId)).Should().BeFalse();
        var credential = await observer.WebAuthnCredentials.AsNoTracking().SingleAsync(x => x.CredentialId == CredentialId);
        credential.SignatureCounter.Should().Be(9);
        credential.LastUsedAt.Should().NotBeNull();
        (await new RefreshTokensStorage(observer, Settings()).GetRefreshTokens(42))
            .Should().ContainSingle(x => x.Value == response.RefreshToken.Value);
        (await observer.PendingNotifications.Select(x => x.Type).ToListAsync())
            .Should().Equal(NotificationType.SuccessfulLogin);
    }

    [Fact]
    public async Task Assertion_ConcurrentSameChallenge_HasOneWinner()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var challengeId = Guid.NewGuid();
        await SeedAssertion(database, challengeId);

        async Task<bool> Attempt()
        {
            await using var context = database.CreateContext();
            var handler = CreateHandler(context, new MetricsCollector(), new VerifyAssertionResult { SignCount = 9 });
            try
            {
                await handler.Handle(Request(challengeId), default);
                return true;
            }
            catch (WebAuthnChallengeExpiredException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Attempt(), Attempt());

        results.Count(x => x).Should().Be(1);
        await using var observer = database.CreateContext();
        (await observer.PendingNotifications.CountAsync()).Should().Be(1);
        (await new RefreshTokensStorage(observer, Settings()).GetRefreshTokens(42)).Should().ContainSingle();
    }

    [Fact]
    public async Task Assertion_RemovedCredentialAfterProof_FailsCasAndRestoresChallenge()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var challengeId = Guid.NewGuid();
        await SeedAssertion(database, challengeId);

        await using var external = database.CreateContext();
        var fido = CreateFido(new VerifyAssertionResult { SignCount = 9 });
        fido.Setup(x => x.MakeAssertionAsync(It.IsAny<MakeAssertionParams>(), It.IsAny<CancellationToken>()))
            .Returns(async (MakeAssertionParams _, CancellationToken _) =>
            {
                await external.WebAuthnCredentials.Where(x => x.CredentialId == CredentialId).ExecuteDeleteAsync();
                return new VerifyAssertionResult { SignCount = 9 };
            });
        await using var context = database.CreateContext();
        var handler = CreateHandler(context, new MetricsCollector(), fido.Object);

        await FluentActions.Awaiting(() => handler.Handle(Request(challengeId), default))
            .Should().ThrowAsync<WebAuthnVerificationFailedException>();

        await using var observer = database.CreateContext();
        (await observer.WebAuthnChallenges.AnyAsync(x => x.Id == challengeId)).Should().BeTrue();
        (await observer.WebAuthnCredentials.AnyAsync(x => x.CredentialId == CredentialId)).Should().BeFalse();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Assertion_CounterChangedAfterProof_FailsCasAndRestoresChallenge()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var challengeId = Guid.NewGuid();
        await SeedAssertion(database, challengeId);

        await using var external = database.CreateContext();
        var fido = CreateFido(new VerifyAssertionResult { SignCount = 9 });
        fido.Setup(x => x.MakeAssertionAsync(It.IsAny<MakeAssertionParams>(), It.IsAny<CancellationToken>()))
            .Returns(async (MakeAssertionParams _, CancellationToken _) =>
            {
                await external.WebAuthnCredentials.Where(x => x.CredentialId == CredentialId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SignatureCounter, 5));
                return new VerifyAssertionResult { SignCount = 9 };
            });
        await using var context = database.CreateContext();

        await FluentActions.Awaiting(() => CreateHandler(context, new MetricsCollector(), fido.Object)
                .Handle(Request(challengeId), default))
            .Should().ThrowAsync<WebAuthnVerificationFailedException>();

        await using var observer = database.CreateContext();
        (await observer.WebAuthnChallenges.AnyAsync(x => x.Id == challengeId)).Should().BeTrue();
        (await observer.WebAuthnCredentials.AsNoTracking().SingleAsync(x => x.CredentialId == CredentialId))
            .SignatureCounter.Should().Be(5);
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
        (await new RefreshTokensStorage(observer, Settings()).GetRefreshTokens(42))
            .Select(x => x.Value).Should().Equal(OldRefresh);
    }

    [Fact]
    public async Task Assertion_ZeroSignatureCounter_IsAccepted()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var challengeId = Guid.NewGuid();
        await SeedAssertion(database, challengeId);
        await using (var update = database.CreateContext())
        {
            await update.WebAuthnCredentials.Where(x => x.CredentialId == CredentialId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SignatureCounter, 0));
        }

        await using var context = database.CreateContext();
        var handler = CreateHandler(context, new MetricsCollector(), new VerifyAssertionResult { SignCount = 0 });

        await handler.Handle(Request(challengeId), default);

        await using var observer = database.CreateContext();
        (await observer.WebAuthnCredentials.AsNoTracking().SingleAsync(x => x.CredentialId == CredentialId))
            .SignatureCounter.Should().Be(0);
        (await observer.WebAuthnChallenges.AnyAsync(x => x.Id == challengeId)).Should().BeFalse();
    }

    [Fact]
    public async Task Assertion_DifferentChallengesWithSameCounter_HaveOneCasWinner()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var firstChallengeId = Guid.NewGuid();
        await SeedAssertion(database, firstChallengeId);
        var secondChallengeId = Guid.NewGuid();
        await using (var setup = database.CreateContext())
        {
            setup.WebAuthnChallenges.Add(CreateAssertionChallenge(secondChallengeId));
            await setup.SaveChangesAsync();
        }

        async Task<bool> Attempt(Guid id)
        {
            await using var context = database.CreateContext();
            var handler = CreateHandler(context, new MetricsCollector(), new VerifyAssertionResult { SignCount = 9 });
            try
            {
                await handler.Handle(Request(id), default);
                return true;
            }
            catch (WebAuthnVerificationFailedException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Attempt(firstChallengeId), Attempt(secondChallengeId));

        results.Count(x => x).Should().Be(1);
        await using var observer = database.CreateContext();
        (await observer.WebAuthnCredentials.AsNoTracking().SingleAsync(x => x.CredentialId == CredentialId))
            .SignatureCounter.Should().Be(9);
        (await observer.PendingNotifications.CountAsync()).Should().Be(1);
        (await new RefreshTokensStorage(observer, Settings()).GetRefreshTokens(42)).Should().ContainSingle();
    }

    private static async Task<DateTime> SeedAssertion(PostgresIdentityDatabase database, Guid challengeId)
    {
        await using var setup = database.CreateContext();
        var expiresAt = DateTime.UtcNow.AddMinutes(5);
        setup.WebAuthnChallenges.Add(CreateAssertionChallenge(challengeId, expiresAt));
        setup.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            UserId = 42,
            CredentialId = CredentialId,
            PublicKey = [7, 8, 9],
            SignatureCounter = 4,
            Name = "test key",
            CreatedAt = DateTime.UtcNow
        });
        await new RefreshTokensStorage(setup, Settings()).CreateNewRefreshToken(OldRefresh, 42, "device-1", 1);
        await setup.SaveChangesAsync();
        return expiresAt;
    }

    private static WebAuthnChallenge CreateAssertionChallenge(Guid challengeId, DateTime? expiresAt = null)
    {
        var options = new Fido2(new Fido2Configuration
        {
            ServerDomain = "localhost",
            ServerName = "Identity tests",
            Origins = new HashSet<string> { "https://localhost" }
        }).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required
        });
        return new WebAuthnChallenge
        {
            Id = challengeId,
            UserId = 0,
            Type = WebAuthnChallengeType.Assertion,
            OptionsJson = options.ToJson(),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(5)
        };
    }

    private static CompleteWebAuthnAssertionCommandHandler CreateHandler(IdentityContext context, MetricsCollector metrics,
        VerifyAssertionResult result)
        => CreateHandler(context, metrics, CreateFido(result).Object);

    private static CompleteWebAuthnAssertionCommandHandler CreateHandler(IdentityContext context, MetricsCollector metrics,
        IFido2 fido)
    {
        var refreshTokens = new RefreshTokensStorage(context, Settings());
        var tokenHandler = new CreateTokenCommandHandler(refreshTokens, new JwtService(Settings()), metrics,
            NullLogger<CreateTokenCommandHandler>.Instance);
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .Returns((CreateTokenCommand command, CancellationToken cancellationToken) => tokenHandler.Handle(command, cancellationToken));
        var users = new Mock<UsersServerApi.UsersServerApiClient>();
        users.Setup(c => c.RegisterDeviceAsync(It.IsAny<RegisterDeviceRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new RegisterDeviceResponse()));
        var location = new Mock<LocationClient>(new HttpClient(), metrics, NullLogger<LocationClient>.Instance);
        location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
        var requestContext = new RequestContext
        {
            DeviceId = "device-1", DeviceName = "Phone", OperationSystem = "Android",
            AppName = "BarkCloud", AppVersion = "1.0", IpAddress = "1.1.1.1"
        };
        var issuer = new SessionIssuer(users.Object, mediator.Object,
            new NotificationOutbox(context, new ConfigurationBuilder().Build(), metrics,
                NullLogger<NotificationOutbox>.Instance),
            refreshTokens, context, requestContext, location.Object, metrics, NullLogger<SessionIssuer>.Instance);
        var rateLimiter = new Mock<IAuthRateLimiter>();
        return new CompleteWebAuthnAssertionCommandHandler(new WebAuthnStorage(context), fido, issuer,
            rateLimiter.Object, metrics, NullLogger<CompleteWebAuthnAssertionCommandHandler>.Instance);
    }

    private static Mock<IFido2> CreateFido(VerifyAssertionResult result)
    {
        var fido = new Mock<IFido2>();
        fido.Setup(x => x.MakeAssertionAsync(It.IsAny<MakeAssertionParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return fido;
    }

    private static CompleteWebAuthnAssertionCommand Request(Guid challengeId) => new()
    {
        ChallengeId = challengeId.ToString(),
        AssertionJson = "{\"id\":\"AQID\",\"rawId\":\"AQID\"}"
    };

    private static JwtSettings Settings() => new()
    {
        SecretKey = "supersecretkey_at_least_32_chars_long_for_hs256!!",
        Issuer = "bark", Audience = "bark", ExpiryMinutes = 60
    };
}
