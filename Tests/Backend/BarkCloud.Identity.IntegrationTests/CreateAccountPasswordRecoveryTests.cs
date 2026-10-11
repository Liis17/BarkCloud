using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.ConfirmAccount;
using BarkCloud.Identity.Features.CreateAccount;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Exceptions.Users;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using Grpc.Core;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.IntegrationTests;

public class CreateAccountPasswordRecoveryTests
{
    [Fact]
    public async Task SmtpDisabledUsersFailure_LeavesProtectedDraft_LegacyIsBlocked_AndPasswordRecoveryCompletes()
    {
        const long userId = 42;
        const string password = "secret-password";
        var users = new Mock<UsersServerApi.UsersServerApiClient>();
        var rateLimiter = new Mock<IAuthRateLimiter>();
        rateLimiter.Setup(r => r.TryReserveAsync(It.IsAny<AuthLimits.Policy>(), It.IsAny<string>())).ReturnsAsync(true);
        rateLimiter.Setup(r => r.ResetAsync(It.IsAny<AuthLimits.Policy>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var addDraftCalls = 0;
        users.Setup(c => c.AddDraftUserAsync(It.IsAny<AddDraftUserRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref addDraftCalls) == 1
                ? GrpcCallHelpers.AsyncUnary(new AddDraftUserResponse { UserId = userId })
                : throw new UserIsDraftException());
        users.Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse { User = new User { Id = userId } }));

        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var registrationCodeId = Guid.NewGuid();
        await SeedRegistrationCode(database, registrationCodeId, userId);
        var confirmCalls = 0;
        var observedTokenCounts = new List<int>();
        var observedHashes = new List<string?>();
        var unavailable = new RpcException(new Status(StatusCode.Unavailable, "Users unavailable"));
        users.Setup(c => c.ConfirmUserAsync(It.Is<ConfirmUserRequest>(r => r.UserId == userId),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns((ConfirmUserRequest _, Metadata _, DateTime? _, CancellationToken _) =>
            {
                var call = Interlocked.Increment(ref confirmCalls);
                return Unary(ObserveAndConfirm(call));
            });

        async Task<ConfirmUserResponse> ObserveAndConfirm(int call)
        {
            await using var observer = database.CreateContext();
            observedHashes.Add(await new PasswordsStorage(observer).GetUserPasswordHash(userId));
            observedTokenCounts.Add(await observer.RefreshTokens.AsNoTracking().CountAsync(x => x.UserId == userId));
            if (call == 1)
                throw unavailable;
            return new ConfirmUserResponse();
        }

        await using (var context = database.CreateContext())
        {
            var create = PasswordCreate(password);
            await FluentActions.Awaiting(() => CreateHandler(context, users.Object, rateLimiter).Handle(create, default))
                .Should().ThrowAsync<RpcException>();
        }

        await using (var observer = database.CreateContext())
        {
            var savedHash = await new PasswordsStorage(observer).GetUserPasswordHash(userId);
            savedHash.Should().NotBeNullOrWhiteSpace();
            PasswordHasher.VerifyPassword(password, savedHash).Should().BeTrue();
            (await observer.RefreshTokens.AsNoTracking().CountAsync(x => x.UserId == userId)).Should().Be(1);
            observedHashes.Should().ContainSingle(hash => hash == savedHash);
            observedTokenCounts.Should().ContainSingle(count => count == 1);
        }

        await using (var context = database.CreateContext())
        {
            var legacy = PasswordCreate(password);
            legacy.RequiresInitialPassword = false;
            legacy.InitialPassword = null;
            await FluentActions.Awaiting(() => CreateHandler(context, users.Object, rateLimiter).Handle(legacy, default))
                .Should().ThrowAsync<UserIsDraftException>();
        }

        await using (var observer = database.CreateContext())
        {
            (await observer.RefreshTokens.AsNoTracking().CountAsync(x => x.UserId == userId)).Should().Be(1);
        }
        users.Verify(c => c.OverrideDraftUserAsync(It.IsAny<AddDraftUserRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);

        await using (var context = database.CreateContext())
        {
            await FluentActions.Awaiting(() => ConfirmHandler(context, users.Object).Handle(
                    new ConfirmAccountCommand { CodeId = registrationCodeId.ToString(), Code = "123456" }, default))
                .Should().ThrowAsync<UserIsDraftException>();
        }
        users.Verify(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        await using (var observer = database.CreateContext())
        {
            (await observer.ConfirmationCodes.AsNoTracking().AnyAsync(x => x.Id == registrationCodeId)).Should().BeTrue();
            (await observer.RefreshTokens.AsNoTracking().CountAsync(x => x.UserId == userId)).Should().Be(1);
        }

        await using (var context = database.CreateContext())
        {
            var wrongPassword = PasswordCreate("wrong-password");
            await FluentActions.Awaiting(() => CreateHandler(context, users.Object, rateLimiter).Handle(wrongPassword, default))
                .Should().ThrowAsync<InvalidLoginOrPasswordException>();
        }

        users.Verify(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        users.Verify(c => c.OverrideDraftUserAsync(It.IsAny<AddDraftUserRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        await using (var observer = database.CreateContext())
        {
            var savedHash = await new PasswordsStorage(observer).GetUserPasswordHash(userId);
            PasswordHasher.VerifyPassword(password, savedHash).Should().BeTrue();
            (await observer.RefreshTokens.AsNoTracking().CountAsync(x => x.UserId == userId)).Should().Be(1);
        }

        await using (var context = database.CreateContext())
        {
            var response = await CreateHandler(context, users.Object, rateLimiter, emailEnabled: true)
                .Handle(PasswordCreate(password), default);
            response.RefreshToken.Value.Should().NotBeNullOrWhiteSpace();
        }

        rateLimiter.Verify(r => r.ResetAsync(AuthLimits.LoginByAccount, userId.ToString(),
            It.IsAny<CancellationToken>()), Times.Once);
        observedTokenCounts.Should().Equal(1, 1);
        observedHashes.Should().OnlyContain(hash => !string.IsNullOrWhiteSpace(hash));
        users.Verify(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        await using var final = database.CreateContext();
        var finalHash = await new PasswordsStorage(final).GetUserPasswordHash(userId);
        finalHash.Should().Be(observedHashes[0]);
        PasswordHasher.VerifyPassword(password, finalHash).Should().BeTrue();
        (await final.RefreshTokens.AsNoTracking().CountAsync(x => x.UserId == userId)).Should().Be(2);
    }

    private static CreateAccountCommand PasswordCreate(string password) => new()
    {
        Username = "user",
        Email = "user@example.test",
        FirstName = "First",
        LastName = "Last",
        InitialPassword = password,
        RequiresInitialPassword = true
    };

    private static async Task SeedRegistrationCode(PostgresIdentityDatabase database, Guid codeId, long userId)
    {
        await using var context = database.CreateContext();
        context.ConfirmationCodes.Add(new ConfirmationCode
        {
            Id = codeId,
            OwnerId = userId,
            Type = ConfirmationCodeType.Registration,
            Value = "123456",
            Expires = DateTime.UtcNow.AddMinutes(5)
        });
        await context.SaveChangesAsync();
    }

    private static CreateAccountCommandHandler CreateHandler(
        BarkCloud.Identity.Persistence.Contexts.IdentityContext context,
        UsersServerApi.UsersServerApiClient users,
        Mock<IAuthRateLimiter>? rateLimiter = null,
        bool emailEnabled = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Features:EmailEnabled"] = emailEnabled.ToString().ToLowerInvariant() })
            .Build();
        var metrics = new MetricsCollector();
        var requestContext = new RequestContext
        {
            DeviceName = "Pixel", DeviceId = "device-1", IpAddress = "203.0.113.5",
            OperationSystem = "Android", AppName = "BarkCloud", AppVersion = "1.0"
        };
        var location = new LocationClient(new HttpClient(), metrics, NullLogger<LocationClient>.Instance);
        var notifications = new NotificationQueueSender(Mock.Of<IPublishEndpoint>(), configuration);
        var policy = new Mock<IRegistrationPolicy>();
        policy.Setup(p => p.EnsureRegistrationEnabledAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        rateLimiter ??= new Mock<IAuthRateLimiter>();
        rateLimiter.Setup(r => r.EnsureSourceAsync(AuthLimits.CreateAccountByIp)).Returns(Task.CompletedTask);
        rateLimiter.Setup(r => r.TryReserveAsync(It.IsAny<AuthLimits.Policy>(), It.IsAny<string>())).ReturnsAsync(true);
        rateLimiter.Setup(r => r.ResetAsync(It.IsAny<AuthLimits.Policy>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return new CreateAccountCommandHandler(users, new ConfirmationCodesStorage(context), notifications,
            requestContext, location, metrics, new RefreshTokensStorage(context, new JwtSettings()),
            new PasswordsStorage(context), context, configuration, policy.Object, rateLimiter.Object,
            NullLogger<CreateAccountCommandHandler>.Instance);
    }

    private static ConfirmAccountCommandHandler ConfirmHandler(
        BarkCloud.Identity.Persistence.Contexts.IdentityContext context,
        UsersServerApi.UsersServerApiClient users)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:EmailEnabled"] = "false" })
            .Build();
        var metrics = new MetricsCollector();
        var policy = new Mock<IRegistrationPolicy>();
        policy.Setup(p => p.EnsureRegistrationEnabledAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var rateLimiter = new Mock<IAuthRateLimiter>();
        rateLimiter.Setup(r => r.EnsureSourceAsync(AuthLimits.ConfirmAccountByIp)).Returns(Task.CompletedTask);
        var requestContext = new RequestContext
        {
            DeviceName = "Pixel", DeviceId = "device-1", IpAddress = "203.0.113.5",
            OperationSystem = "Android", AppName = "BarkCloud", AppVersion = "1.0"
        };
        var outbox = new NotificationOutbox(context, configuration, metrics,
            NullLogger<NotificationOutbox>.Instance);

        return new ConfirmAccountCommandHandler(new ConfirmationCodesStorage(context), users,
            new RefreshTokensStorage(context, new JwtSettings()), context, requestContext,
            new PasswordsStorage(context), outbox, metrics, policy.Object, rateLimiter.Object,
            NullLogger<ConfirmAccountCommandHandler>.Instance);
    }

    private static AsyncUnaryCall<ConfirmUserResponse> Unary(Task<ConfirmUserResponse> response) => new(
        response,
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => new Metadata(),
        () => { });
}
