using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests.Persistence;

public class AuthPropertiesStorageEmailCodeTests : IDisposable
{
    private const long UserId = 7;
    private const string Code = "123456";

    private readonly SqliteIdentityContext _db = new();
    private readonly AuthPropertiesStorage _sut;

    public AuthPropertiesStorageEmailCodeTests()
    {
        _sut = new AuthPropertiesStorage(_db.Context);
    }

    public void Dispose() => _db.Dispose();

    private async Task<AuthUserProperty> Reload()
    {
        using var ctx = _db.CreateAdditionalContext();
        return await ctx.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == UserId);
    }

    private async Task<bool> Consume(EmailAuthCodePurpose purpose, string? code)
    {
        var validated = await _sut.TryValidateAndReserveEmailAuthCode(UserId, purpose, code);
        return validated is not null && await _sut.TryConsumeValidatedEmailAuthCode(validated);
    }

    private async Task Mutate(Action<AuthUserProperty> change)
    {
        using var ctx = _db.CreateAdditionalContext();
        var props = await ctx.AuthUserProperties.SingleAsync(x => x.UserId == UserId);
        change(props);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Issue_NoRow_CreatesRowWithCodeAndExpiry()
    {
        var issued = await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        issued.Should().BeTrue();
        var props = await Reload();
        props.LastEmailAuthCode.Should().Be(Code);
        props.EmailAuthCodePurpose.Should().Be(EmailAuthCodePurpose.Login);
        props.EmailAuthCodeAttempts.Should().Be(0);
        props.EmailAuthCodeExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Consume_ValidCode_SucceedsOnlyOnce()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        (await Consume(EmailAuthCodePurpose.Login, Code)).Should().BeTrue();
        (await Consume(EmailAuthCodePurpose.Login, Code)).Should().BeFalse();

        var props = await Reload();
        props.LastEmailAuthCode.Should().BeNull();
        props.EmailAuthCodeExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Consume_ExpiredCode_Fails()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);
        await Mutate(p => p.EmailAuthCodeExpiresAt = DateTime.UtcNow.AddMinutes(-1));

        (await Consume(EmailAuthCodePurpose.Login, Code)).Should().BeFalse();
    }

    [Fact]
    public async Task Consume_OtherPurpose_FailsAndKeepsCode()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        (await Consume(EmailAuthCodePurpose.EnableEmailOtp, Code)).Should().BeFalse();

        // Код входа по-прежнему пригоден для входа.
        (await Consume(EmailAuthCodePurpose.Login, Code)).Should().BeTrue();
    }

    [Fact]
    public async Task Consume_WrongCode_Fails()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        (await Consume(EmailAuthCodePurpose.Login, "000000")).Should().BeFalse();
        (await Consume(EmailAuthCodePurpose.Login, null)).Should().BeFalse();
    }

    [Fact]
    public async Task Consume_AfterFiveWrongAttempts_RejectsEvenCorrectCode()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        for (var i = 0; i < 5; i++)
        {
            (await Consume(EmailAuthCodePurpose.Login, "000000")).Should().BeFalse();
        }

        (await Consume(EmailAuthCodePurpose.Login, Code)).Should().BeFalse();
    }

    [Fact]
    public async Task Consume_FourWrongAttempts_StillAcceptsCorrectCode()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        for (var i = 0; i < 4; i++)
        {
            await Consume(EmailAuthCodePurpose.Login, "000000");
        }

        (await Consume(EmailAuthCodePurpose.Login, Code)).Should().BeTrue();
    }

    [Fact]
    public async Task Consume_LastReservedAttempt_CanConsumeValidatedCode()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        for (var i = 0; i < 4; i++)
        {
            (await _sut.TryValidateAndReserveEmailAuthCode(UserId, EmailAuthCodePurpose.Login, "000000"))
                .Should().BeNull();
        }

        var validated = await _sut.TryValidateAndReserveEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        validated.Should().NotBeNull();
        (await Reload()).EmailAuthCodeAttempts.Should().Be(5);
        (await _sut.TryConsumeValidatedEmailAuthCode(validated!)).Should().BeTrue();
    }

    [Fact]
    public async Task Consume_ReissuedCodeDoesNotConsumePreviouslyValidatedIssuance()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);
        var validated = await _sut.TryValidateAndReserveEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);
        validated.Should().NotBeNull();

        await Mutate(p =>
        {
            p.LastEmailAuthCode = "654321";
            p.EmailAuthCodeIssuedAt = DateTime.UtcNow.AddSeconds(1);
            p.EmailAuthCodeExpiresAt = DateTime.UtcNow.AddMinutes(6);
            p.EmailAuthCodeAttempts = 0;
        });

        (await _sut.TryConsumeValidatedEmailAuthCode(validated!)).Should().BeFalse();
        var properties = await Reload();
        properties.LastEmailAuthCode.Should().Be("654321");
        properties.EmailAuthCodeAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Consume_LegacyCodeWithoutExpiry_Fails()
    {
        // Состояние после миграции: код есть, срока и назначения нет.
        _db.Context.AuthUserProperties.Add(new AuthUserProperty { UserId = UserId, LastEmailAuthCode = Code });
        await _db.Context.SaveChangesAsync();

        (await Consume(EmailAuthCodePurpose.Login, Code)).Should().BeFalse();
    }

    [Fact]
    public async Task Issue_WithinCooldown_ReturnsFalseAndKeepsPreviousCode()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        var issued = await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, "654321");

        issued.Should().BeFalse();
        (await Reload()).LastEmailAuthCode.Should().Be(Code);
    }

    [Fact]
    public async Task Issue_AfterCooldown_ReplacesCodeAndResetsAttempts()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);
        await Consume(EmailAuthCodePurpose.Login, "000000");
        await Mutate(p => p.EmailAuthCodeIssuedAt = DateTime.UtcNow.AddSeconds(-61));

        var issued = await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, "654321");

        issued.Should().BeTrue();
        var props = await Reload();
        props.LastEmailAuthCode.Should().Be("654321");
        props.EmailAuthCodeAttempts.Should().Be(0);
        (await Consume(EmailAuthCodePurpose.Login, Code)).Should().BeFalse();
    }

    [Fact]
    public async Task Issue_OtherPurposeWithinCooldown_ReplacesCode()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        var issued = await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.EnableEmailOtp, "654321");

        issued.Should().BeTrue();
        (await Consume(EmailAuthCodePurpose.Login, Code)).Should().BeFalse();
        (await Consume(EmailAuthCodePurpose.EnableEmailOtp, "654321")).Should().BeTrue();
    }

    [Fact]
    public async Task Issue_ExistingRowWithLegacyCode_IsNotBlockedByCooldown()
    {
        _db.Context.AuthUserProperties.Add(new AuthUserProperty { UserId = UserId, LastEmailAuthCode = "111111" });
        await _db.Context.SaveChangesAsync();

        var issued = await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        issued.Should().BeTrue();
        (await Reload()).LastEmailAuthCode.Should().Be(Code);
    }

    [Fact]
    public async Task Consume_ParallelRequestsWithSameCode_OnlyOneSucceeds()
    {
        await _sut.TryIssueEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);

        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var ctx = _db.CreateAdditionalContext();
            var storage = new AuthPropertiesStorage(ctx);
            var validated = await storage.TryValidateAndReserveEmailAuthCode(UserId, EmailAuthCodePurpose.Login, Code);
            return validated is not null && await storage.TryConsumeValidatedEmailAuthCode(validated);
        }));

        var results = await Task.WhenAll(attempts);

        results.Count(r => r).Should().Be(1);
    }
}
