using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests.Persistence;

/// <summary>Счётчики попыток на самом запросе сброса пароля и коде регистрации (F14).</summary>
public class ChallengeAttemptsStorageTests : IDisposable
{
    private const int Max = 5;

    private readonly SqliteIdentityContext _db = new();
    private readonly ResetPasswordsStorage _resets;
    private readonly ConfirmationCodesStorage _codes;

    public ChallengeAttemptsStorageTests()
    {
        _resets = new ResetPasswordsStorage(_db.Context);
        _codes = new ConfirmationCodesStorage(_db.Context);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Guid> AddReset(Action<ResetPassword>? configure = null, long userId = 42)
    {
        var reset = new ResetPassword
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            OtpType = OtpType.Email,
            OtpCode = "123456"
        };
        configure?.Invoke(reset);
        using var ctx = _db.CreateAdditionalContext();
        ctx.ResetPasswords.Add(reset);
        await ctx.SaveChangesAsync();
        return reset.Id;
    }

    private async Task<Guid> AddCode(Action<ConfirmationCode>? configure = null)
    {
        var code = new ConfirmationCode
        {
            Id = Guid.NewGuid(),
            Value = "123456",
            Expires = DateTime.UtcNow.AddHours(1),
            OwnerId = 7,
            Type = ConfirmationCodeType.Registration
        };
        configure?.Invoke(code);
        using var ctx = _db.CreateAdditionalContext();
        ctx.ConfirmationCodes.Add(code);
        await ctx.SaveChangesAsync();
        return code.Id;
    }

    private async Task<int> ResetAttempts(Guid id)
    {
        using var ctx = _db.CreateAdditionalContext();
        return (await ctx.ResetPasswords.AsNoTracking().SingleAsync(x => x.Id == id)).OtpAttempts;
    }

    private async Task<int> CodeAttempts(Guid id)
    {
        using var ctx = _db.CreateAdditionalContext();
        return (await ctx.ConfirmationCodes.AsNoTracking().SingleAsync(x => x.Id == id)).Attempts;
    }

    // ───────── ResetPassword.OtpAttempts ─────────

    [Fact]
    public async Task ResetAttempt_AllowsMaxThenBlocks()
    {
        var id = await AddReset();

        for (var i = 0; i < Max; i++)
        {
            (await _resets.TryReserveOtpAttempt(id, Max)).Should().BeTrue($"попытка {i + 1} в пределах лимита");
        }

        (await _resets.TryReserveOtpAttempt(id, Max)).Should().BeFalse();
        (await ResetAttempts(id)).Should().Be(Max);
    }

    [Fact]
    public async Task ResetAttempt_LimitIsPerResetNotPerUser()
    {
        var first = await AddReset();
        var second = await AddReset();
        for (var i = 0; i < Max; i++) await _resets.TryReserveOtpAttempt(first, Max);

        (await _resets.TryReserveOtpAttempt(first, Max)).Should().BeFalse();
        (await _resets.TryReserveOtpAttempt(second, Max)).Should().BeTrue();
    }

    [Fact]
    public async Task ResetAttempt_ApprovedExpiredOrUnknown_IsRejected()
    {
        var approved = await AddReset(r => r.IsApproved = true);
        var expired = await AddReset(r => r.ExpiresAt = DateTime.UtcNow.AddMinutes(-1));

        (await _resets.TryReserveOtpAttempt(approved, Max)).Should().BeFalse();
        (await _resets.TryReserveOtpAttempt(expired, Max)).Should().BeFalse();
        (await _resets.TryReserveOtpAttempt(Guid.NewGuid(), Max)).Should().BeFalse();
    }

    [Fact]
    public async Task ResetAttempt_ParallelRequests_AllowExactlyMax()
    {
        var id = await AddReset();

        var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(async _ =>
        {
            using var ctx = _db.CreateAdditionalContext();
            return await new ResetPasswordsStorage(ctx).TryReserveOtpAttempt(id, Max);
        }));

        results.Count(r => r).Should().Be(Max);
        (await ResetAttempts(id)).Should().Be(Max);
    }

    // ───────── GetActiveEmailReset ─────────

    [Fact]
    public async Task ActiveEmailReset_ReturnsLatestUnusedUnexpiredRequestOfUser()
    {
        await AddReset(r => r.CreatedAt = DateTime.UtcNow.AddMinutes(-3));
        var latest = await AddReset(r => r.CreatedAt = DateTime.UtcNow.AddMinutes(-1));
        await AddReset(userId: 99);

        (await _resets.GetActiveEmailReset(42))!.Id.Should().Be(latest);
    }

    [Fact]
    public async Task ActiveEmailReset_SkipsApprovedExpiredAuthenticatorAndExhausted()
    {
        await AddReset(r => r.IsApproved = true);
        await AddReset(r => r.ExpiresAt = DateTime.UtcNow.AddSeconds(-1));
        await AddReset(r => r.OtpType = OtpType.Authenticator);
        await AddReset(r => r.OtpAttempts = Max);

        (await _resets.GetActiveEmailReset(42)).Should().BeNull();
    }

    // ───────── ConfirmationCode.Attempts ─────────

    [Fact]
    public async Task CodeAttempt_AllowsMaxThenBlocks()
    {
        var id = await AddCode();

        for (var i = 0; i < Max; i++)
        {
            (await _codes.TryReserveAttempt(id, Max)).Should().BeTrue($"попытка {i + 1} в пределах лимита");
        }

        (await _codes.TryReserveAttempt(id, Max)).Should().BeFalse();
        (await CodeAttempts(id)).Should().Be(Max);
    }

    [Fact]
    public async Task CodeAttempt_ExpiredOrUnknown_IsRejected()
    {
        var expired = await AddCode(c => c.Expires = DateTime.UtcNow.AddMinutes(-1));

        (await _codes.TryReserveAttempt(expired, Max)).Should().BeFalse();
        (await _codes.TryReserveAttempt(Guid.NewGuid(), Max)).Should().BeFalse();
    }

    [Fact]
    public async Task CodeAttempt_ParallelRequests_AllowExactlyMax()
    {
        var id = await AddCode();

        var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(async _ =>
        {
            using var ctx = _db.CreateAdditionalContext();
            return await new ConfirmationCodesStorage(ctx).TryReserveAttempt(id, Max);
        }));

        results.Count(r => r).Should().Be(Max);
        (await CodeAttempts(id)).Should().Be(Max);
    }
}
