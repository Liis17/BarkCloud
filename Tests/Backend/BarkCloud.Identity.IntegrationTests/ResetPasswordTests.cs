using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Features.ConfirmResetPassword;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;

using MassTransit;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using DomainResetPassword = BarkCloud.Identity.Domain.ResetPassword;
using OtpType = BarkCloud.Identity.Domain.OtpType;

using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;

namespace BarkCloud.Identity.IntegrationTests;

public class ResetPasswordTests
{
    [Fact]
    public async Task ResetPassword_CommitCanceled_PreservesUnusedResetAndOldSessions()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var reset = await Seed(setup);
        using var cancellation = new CancellationTokenSource();
        await using var context = database.CreateContext(new CancelCommit(cancellation));

        await FluentActions.Awaiting(() => CreateHandler(context).Handle(Command(reset.Id), cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        await using var reader = database.CreateContext();
        (await new ResetPasswordsStorage(reader).GetResetPassword(reset.Id))!.IsApproved.Should().BeFalse();
        PasswordHasher.VerifyPassword("old-password", await new PasswordsStorage(reader).GetUserPasswordHash(42))
            .Should().BeTrue();
        (await new RefreshTokensStorage(reader, new JwtSettings()).GetRefreshTokens(42)).Select(x => x.Value)
            .Should().BeEquivalentTo("old-current", "old-other");
        (await reader.RevokedSessions.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ResetPassword_WhenNewRefreshCannotBeSaved_RollsBackResetPasswordAndRevocations()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var reset = await Seed(context);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_new_refresh() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'new refresh write failed'; END $$;
            CREATE TRIGGER reject_new_refresh BEFORE INSERT ON "RefreshTokens"
            FOR EACH ROW EXECUTE FUNCTION reject_new_refresh();
            """);

        await FluentActions.Awaiting(() => CreateHandler(context).Handle(Command(reset.Id), default))
            .Should().ThrowAsync<DbUpdateException>();

        await using var reader = database.CreateContext();
        (await new ResetPasswordsStorage(reader).GetResetPassword(reset.Id))!.IsApproved.Should().BeFalse();
        PasswordHasher.VerifyPassword("old-password", await new PasswordsStorage(reader).GetUserPasswordHash(42))
            .Should().BeTrue();
        (await new RefreshTokensStorage(reader, new JwtSettings()).GetRefreshTokens(42))
            .Select(t => t.Value).Should().BeEquivalentTo("old-current", "old-other");
        (await reader.RevokedSessions.AnyAsync()).Should().BeFalse();

        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_new_refresh ON \"RefreshTokens\";");
        await using var retry = database.CreateContext();
        await CreateHandler(retry).Handle(Command(reset.Id), default);
        reader.ChangeTracker.Clear();
        (await new ResetPasswordsStorage(reader).GetResetPassword(reset.Id))!.IsApproved.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetPassword_CommitsNewPasswordAndTokens_AndHonorsRevocationChoice(bool revoke)
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var reset = await Seed(context);
        await using var beforeReset = database.CreateContext();
        var oldIds = (await new RefreshTokensStorage(beforeReset, new JwtSettings()).GetRefreshTokens(42))
            .ToDictionary(x => x.Value, x => x.Id);
        var committedBeforeNotification = false;
        var handler = CreateHandler(context, async () =>
        {
            await using var observer = database.CreateContext();
            committedBeforeNotification = (await new ResetPasswordsStorage(observer).GetResetPassword(reset.Id))!.IsApproved;
            throw new InvalidOperationException("notification unavailable");
        });

        var response = await handler.Handle(Command(reset.Id, revoke), default);

        committedBeforeNotification.Should().BeTrue();
        await using var reader = database.CreateContext();
        PasswordHasher.VerifyPassword("new-password", await new PasswordsStorage(reader).GetUserPasswordHash(42))
            .Should().BeTrue();
        var tokens = await new RefreshTokensStorage(reader, new JwtSettings()).GetRefreshTokens(42);
        tokens.Should().Contain(t => t.Value == response.RefreshToken.Value && t.DeviceId == "current");
        tokens.Count.Should().Be(revoke ? 1 : 3);
        var revoked = await reader.RevokedSessions.ToListAsync();
        if (revoke)
        {
            revoked.Select(x => x.DeviceId).Should().BeEquivalentTo("other", "current");
            // Все устройства отзываются по порогу сессии (Id удалённого refresh); у новой сессии Id строго больше.
            revoked.Single(x => x.DeviceId == "other").MaxSessionId.Should().Be(oldIds["old-other"]);
            var oldCurrentId = revoked.Single(x => x.DeviceId == "current").MaxSessionId;
            oldCurrentId.Should().Be(oldIds["old-current"]);
            tokens.Single().Id.Should().BeGreaterThan(oldCurrentId!.Value);
        }
        else
        {
            revoked.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ResetPassword_OldAccessOfCurrentDeviceIsRevoked_WhileNewPairWorks()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var reset = await Seed(context);
        var settings = new JwtSettings
        {
            SecretKey = "supersecretkey_at_least_32_chars_long_for_hs256!!", Issuer = "bark", Audience = "bark", ExpiryMinutes = 60
        };
        var jwt = new JwtService(settings);
        var storage = new RefreshTokensStorage(context, settings);
        var oldCurrent = (await storage.FindRefreshToken("old-current"))!;
        var oldOther = (await storage.FindRefreshToken("old-other"))!;
        // Копии access-токенов, выданные до сброса пароля.
        var oldCurrentAccess = jwt.GenerateUserToken(42, "current", oldCurrent.Id).Value;
        var oldOtherAccess = jwt.GenerateUserToken(42, "other", oldOther.Id).Value;
        context.ChangeTracker.Clear();

        var response = await CreateHandler(context, jwt: jwt).Handle(Command(reset.Id), default);

        var cache = await LoadCache(database);

        IsRevoked(cache, oldCurrentAccess, "current").Should().BeTrue("старый access текущего устройства отозван");
        IsRevoked(cache, oldOtherAccess, "other").Should().BeTrue();
        IsRevoked(cache, response.AccessToken.Value, "current").Should().BeFalse("новая пара работает, даже если выдана в ту же секунду");
        await using var reader = database.CreateContext();
        var refreshStorage = new RefreshTokensStorage(reader, settings);
        (await refreshStorage.FindRefreshToken("old-current")).Should().BeNull("старый refresh удалён");
        (await refreshStorage.FindRefreshToken(response.RefreshToken.Value))!.Id.Should().BeGreaterThan(oldCurrent.Id);
    }

    [Fact]
    public async Task ResetPassword_StaleRefreshReadBeforeReset_IssuedAccessIsRevoked()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var reset = await Seed(context);
        var settings = new JwtSettings
        {
            SecretKey = "supersecretkey_at_least_32_chars_long_for_hs256!!", Issuer = "bark", Audience = "bark", ExpiryMinutes = 60
        };
        var jwt = new JwtService(settings);

        // CreateToken другого устройства прочитал refresh и остановился до сброса пароля.
        var pause = new RefreshReadPause();
        await using var staleContext = database.CreateContext(pause);
        var staleCreateToken = new CreateTokenCommandHandler(new RefreshTokensStorage(staleContext, settings), jwt,
            new MetricsCollector(), NullLogger<CreateTokenCommandHandler>.Instance);
        var stale = staleCreateToken.Handle(new CreateTokenCommand { RefreshToken = "old-other" }, default);
        await pause.Reached;

        var response = await CreateHandler(context, jwt: jwt).Handle(Command(reset.Id), default);
        // iat имеет секундную точность: выпуск после reset должен попасть в секунду позже RevokedAt.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        pause.Release();
        var staleAccess = (await stale).AccessToken.Value;

        var cache = await LoadCache(database);
        IsRevoked(cache, staleAccess, "other").Should().BeTrue("JWT выпущен по refresh, удалённому сбросом пароля");
        IsRevoked(cache, response.AccessToken.Value, "current").Should().BeFalse("новая сессия работает");
    }

    [Fact]
    public async Task ResetPassword_ConcurrentConfirmations_OnlyOneCreatesNewSession()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var reset = await Seed(setup);
        var barrier = new ResetReadBarrier();
        await using var first = database.CreateContext(barrier);
        await using var second = database.CreateContext(barrier);
        var request = Command(reset.Id);

        var a = Capture(() => CreateHandler(first).Handle(request, default));
        var b = Capture(() => CreateHandler(second).Handle(request, default));
        await barrier.Reached;
        barrier.Release();
        var errors = await Task.WhenAll(a, b);

        errors.Count(x => x is null).Should().Be(1);
        errors.Single(x => x is not null).Should().BeOfType<ResetIdHasIsApprovedException>();
        await using var reader = database.CreateContext();
        (await new RefreshTokensStorage(reader, new JwtSettings()).GetRefreshTokens(42)).Should().ContainSingle();
        (await reader.RevokedSessions.Select(x => x.DeviceId).ToListAsync()).Should().BeEquivalentTo("other", "current");
    }

    [Fact]
    public async Task ResetPassword_AfterMaxWrongCodes_RejectsCorrectCodeAndKeepsOldPassword()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var reset = await Seed(setup);

        // Каждая попытка — своя область (контекст), как отдельные запросы; адрес источника в лимите не участвует.
        for (var i = 0; i < AuthLimits.ChallengeMaxAttempts; i++)
        {
            await using var context = database.CreateContext();
            var wrong = Command(reset.Id);
            wrong.OtpCode = $"00000{i}";
            await FluentActions.Awaiting(() => CreateHandler(context).Handle(wrong, default))
                .Should().ThrowAsync<NotValidOtpCodeException>();
        }

        await using var final = database.CreateContext();
        await FluentActions.Awaiting(() => CreateHandler(final).Handle(Command(reset.Id), default))
            .Should().ThrowAsync<NotValidOtpCodeException>();

        await using var reader = database.CreateContext();
        (await new ResetPasswordsStorage(reader).GetResetPassword(reset.Id))!.IsApproved.Should().BeFalse();
        PasswordHasher.VerifyPassword("old-password", await new PasswordsStorage(reader).GetUserPasswordHash(42))
            .Should().BeTrue();
    }

    // Кэш наполняется так же, как в сервисах: из фида Identity.
    private static async Task<TokenRevocationCache> LoadCache(PostgresIdentityDatabase database)
    {
        await using var services = new ServiceCollection().AddScoped(_ => database.CreateContext()).BuildServiceProvider();
        var batch = await new DbRevocationFeed(services.GetRequiredService<IServiceScopeFactory>()).FetchAsync(null, default);
        var cache = new TokenRevocationCache();
        foreach (var session in batch.Sessions)
            cache.Revoke(session.UserId, session.DeviceId, session.RevokedAt, session.ExpiresAt, session.MaxSessionId);
        return cache;
    }

    private static bool IsRevoked(TokenRevocationCache cache, string token, string deviceId)
    {
        var jwtToken = new JwtSecurityTokenHandler().ReadJwtToken(token);
        long? sid = long.TryParse(jwtToken.Claims.Single(x => x.Type == IdentityClaims.SessionId).Value, out var v) ? v : null;
        return cache.IsRevoked(42, deviceId, jwtToken.IssuedAt, sid);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    internal static async Task<DomainResetPassword> Seed(IdentityContext context)
    {
        await new PasswordsStorage(context).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("old-password"));
        var tokens = new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 });
        await tokens.CreateNewRefreshToken("old-current", 42, "current", 1);
        await tokens.CreateNewRefreshToken("old-other", 42, "other", 1);
        return await new ResetPasswordsStorage(context).AddResetPassword(new DomainResetPassword
        {
            Id = Guid.NewGuid(), UserId = 42, CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5), OtpType = OtpType.Email, OtpCode = "123456"
        });
    }

    internal static ConfirmResetPasswordCommand Command(Guid resetId, bool revoke = true) => new()
    {
        ResetId = resetId, OtpCode = "123456", NewPassword = "new-password", RevokeOtherSessions = revoke
    };

    internal static ConfirmResetPasswordCommandHandler CreateHandler(IdentityContext context, Func<Task>? notify = null,
        JwtService? jwt = null)
    {
        var metrics = new MetricsCollector();
        var refreshTokens = new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 });
        var request = new RequestContext
        {
            DeviceId = "current", DeviceName = "Phone", OperationSystem = "Android", AppName = "BarkCloud", AppVersion = "1.0"
        };
        var mediator = new Mock<IMediator>();
        if (jwt is null)
        {
            mediator.Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CreateTokenResponse { AccessToken = new Token { Value = "access" } });
        }
        else
        {
            // Настоящий выпуск access: sid берётся из Id только что созданной refresh-строки.
            var createToken = new CreateTokenCommandHandler(refreshTokens, jwt, metrics, NullLogger<CreateTokenCommandHandler>.Instance);
            mediator.Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
                .Returns((CreateTokenCommand command, CancellationToken token) => createToken.Handle(command, token));
        }
        var notifier = new Mock<PasswordChangedNotifier>(Mock.Of<INotificationOutbox>(), request);
        notifier.Setup(n => n.NotifyAsync(It.IsAny<long>())).Returns(() => notify?.Invoke() ?? Task.CompletedTask);
        return new ConfirmResetPasswordCommandHandler(new ResetPasswordsStorage(context), new AuthPropertiesStorage(context),
            new PasswordsStorage(context), refreshTokens,
            mediator.Object, notifier.Object, request, metrics,
            new AuthRateLimiter(new AttemptCountersStorage(context), request, NullLogger<AuthRateLimiter>.Instance),
            NullLogger<ConfirmResetPasswordCommandHandler>.Instance, context);
    }

    private sealed class CancelCommit(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
