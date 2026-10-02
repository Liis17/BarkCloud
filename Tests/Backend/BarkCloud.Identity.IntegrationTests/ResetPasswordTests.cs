using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
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

using MassTransit;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using DomainResetPassword = BarkCloud.Identity.Domain.ResetPassword;
using OtpType = BarkCloud.Identity.Domain.OtpType;

using System.Data.Common;

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
            revoked.Should().ContainSingle().Which.DeviceId.Should().Be("other");
        else
            revoked.Should().BeEmpty();
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
        (await reader.RevokedSessions.SingleAsync()).DeviceId.Should().Be("other");
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

    internal static ConfirmResetPasswordCommandHandler CreateHandler(IdentityContext context, Func<Task>? notify = null)
    {
        var metrics = new MetricsCollector();
        var request = new RequestContext
        {
            DeviceId = "current", DeviceName = "Phone", OperationSystem = "Android", AppName = "BarkCloud", AppVersion = "1.0"
        };
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTokenResponse { AccessToken = new Token { Value = "access" } });
        var notifier = new Mock<PasswordChangedNotifier>(Mock.Of<UsersServerApi.UsersServerApiClient>(),
            new NotificationQueueSender(Mock.Of<IPublishEndpoint>(), new ConfigurationBuilder().Build()),
            new LocationClient(new HttpClient(), metrics, NullLogger<LocationClient>.Instance),
            request, NullLogger<PasswordChangedNotifier>.Instance);
        notifier.Setup(n => n.NotifyAsync(It.IsAny<long>())).Returns(() => notify?.Invoke() ?? Task.CompletedTask);
        return new ConfirmResetPasswordCommandHandler(new ResetPasswordsStorage(context), new AuthPropertiesStorage(context),
            new PasswordsStorage(context), new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 }),
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
