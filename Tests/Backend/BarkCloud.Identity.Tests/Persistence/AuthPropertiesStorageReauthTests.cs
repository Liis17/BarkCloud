using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests.Persistence;

public class AuthPropertiesStorageReauthTests : IDisposable
{
    private const long UserId = 7;
    private const int MaxAttempts = 5;

    private readonly SqliteIdentityContext _db = new();
    private readonly AuthPropertiesStorage _sut;

    public AuthPropertiesStorageReauthTests()
    {
        _sut = new AuthPropertiesStorage(_db.Context);
    }

    public void Dispose() => _db.Dispose();

    private async Task<AuthUserProperty> Reload()
    {
        using var ctx = _db.CreateAdditionalContext();
        return await ctx.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == UserId);
    }

    private async Task Mutate(Action<AuthUserProperty> change)
    {
        using var ctx = _db.CreateAdditionalContext();
        var props = await ctx.AuthUserProperties.SingleAsync(x => x.UserId == UserId);
        change(props);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Reserve_NoRow_CreatesRowWithFirstAttempt()
    {
        (await _sut.TryReserveReauthPasswordAttempt(UserId)).Should().BeTrue();

        var props = await Reload();
        props.ReauthPasswordAttempts.Should().Be(1);
        props.ReauthPasswordWindowEndsAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(30));
        props.OtpEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Reserve_AllowsMaxAttemptsThenBlocksUntilWindowEnds()
    {
        for (var i = 0; i < MaxAttempts; i++)
        {
            (await _sut.TryReserveReauthPasswordAttempt(UserId)).Should().BeTrue($"попытка {i + 1} в пределах лимита");
        }

        (await _sut.TryReserveReauthPasswordAttempt(UserId)).Should().BeFalse();
        (await _sut.TryReserveReauthPasswordAttempt(UserId)).Should().BeFalse();
        (await Reload()).ReauthPasswordAttempts.Should().Be(MaxAttempts);
    }

    [Fact]
    public async Task Reserve_WindowEnded_StartsNewWindow()
    {
        for (var i = 0; i < MaxAttempts; i++)
        {
            await _sut.TryReserveReauthPasswordAttempt(UserId);
        }
        await Mutate(p => p.ReauthPasswordWindowEndsAt = DateTime.UtcNow.AddMinutes(-1));

        (await _sut.TryReserveReauthPasswordAttempt(UserId)).Should().BeTrue();

        var props = await Reload();
        props.ReauthPasswordAttempts.Should().Be(1);
        props.ReauthPasswordWindowEndsAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Reserve_ExistingRow_DoesNotTouchOtpSettings()
    {
        await _sut.SetPendingOtpSecret(UserId, "PENDING", DateTime.UtcNow.AddMinutes(10));
        await Mutate(p =>
        {
            p.OtpEnabled = true;
            p.OtpSecret = "ACTIVE";
        });

        await _sut.TryReserveReauthPasswordAttempt(UserId);

        var props = await Reload();
        props.OtpEnabled.Should().BeTrue();
        props.OtpSecret.Should().Be("ACTIVE");
        props.PendingOtpSecret.Should().Be("PENDING");
    }

    [Fact]
    public async Task Reset_ClearsAttemptsAndWindow()
    {
        await _sut.TryReserveReauthPasswordAttempt(UserId);
        await _sut.TryReserveReauthPasswordAttempt(UserId);

        await _sut.ResetReauthPasswordAttempts(UserId);

        var props = await Reload();
        props.ReauthPasswordAttempts.Should().Be(0);
        props.ReauthPasswordWindowEndsAt.Should().BeNull();
        (await _sut.TryReserveReauthPasswordAttempt(UserId)).Should().BeTrue();
    }

    [Fact]
    public async Task Reserve_ParallelAttempts_NeverExceedLimit()
    {
        // Строка уже есть: параллельные запросы конкурируют за один условный UPDATE.
        await _sut.TryReserveReauthPasswordAttempt(UserId);
        await _sut.ResetReauthPasswordAttempts(UserId);

        var attempts = Enumerable.Range(0, 20).Select(async _ =>
        {
            using var ctx = _db.CreateAdditionalContext();
            return await new AuthPropertiesStorage(ctx).TryReserveReauthPasswordAttempt(UserId);
        });
        var results = await Task.WhenAll(attempts);

        results.Count(r => r).Should().Be(MaxAttempts);
        (await Reload()).ReauthPasswordAttempts.Should().Be(MaxAttempts);
    }
}
