using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests.Persistence;

public class AuthPropertiesStoragePendingSecretTests : IDisposable
{
    private const long UserId = 7;

    private readonly SqliteIdentityContext _db = new();
    private readonly AuthPropertiesStorage _sut;

    public AuthPropertiesStoragePendingSecretTests()
    {
        _sut = new AuthPropertiesStorage(_db.Context);
    }

    public void Dispose() => _db.Dispose();

    private async Task<AuthUserProperty> Reload()
    {
        using var ctx = _db.CreateAdditionalContext();
        return await ctx.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == UserId);
    }

    private async Task SeedActiveAuthenticator()
    {
        await _sut.SetPendingOtpSecret(UserId, "OLD", DateTime.UtcNow.AddMinutes(10));
        (await _sut.ActivatePendingOtpSecret(UserId, "OLD")).Should().BeTrue();
    }

    [Fact]
    public async Task SetPending_NoRow_CreatesRowWithoutActivatingAuthenticator()
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(10);

        await _sut.SetPendingOtpSecret(UserId, "NEW", expiresAt);

        var props = await Reload();
        props.PendingOtpSecret.Should().Be("NEW");
        props.PendingOtpSecretExpiresAt.Should().BeCloseTo(expiresAt, TimeSpan.FromSeconds(1));
        props.OtpSecret.Should().BeNull();
        props.OtpEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetPending_ActiveAuthenticator_KeepsActiveSecretUntouched()
    {
        await SeedActiveAuthenticator();

        await _sut.SetPendingOtpSecret(UserId, "NEW", DateTime.UtcNow.AddMinutes(10));

        var props = await Reload();
        props.OtpEnabled.Should().BeTrue();
        props.OtpSecret.Should().Be("OLD");
        props.PendingOtpSecret.Should().Be("NEW");
    }

    [Fact]
    public async Task Activate_MatchingSecret_PromotesPendingAndClearsIt()
    {
        await SeedActiveAuthenticator();
        await _sut.SetPendingOtpSecret(UserId, "NEW", DateTime.UtcNow.AddMinutes(10));

        (await _sut.ActivatePendingOtpSecret(UserId, "NEW")).Should().BeTrue();

        var props = await Reload();
        props.OtpEnabled.Should().BeTrue();
        props.OtpSecret.Should().Be("NEW");
        props.PendingOtpSecret.Should().BeNull();
        props.PendingOtpSecretExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Activate_SecretReplacedMeanwhile_FailsAndChangesNothing()
    {
        await SeedActiveAuthenticator();
        await _sut.SetPendingOtpSecret(UserId, "NEWER", DateTime.UtcNow.AddMinutes(10));

        // Код проверяли по "NEW", но за это время ожидающий секрет заменили на "NEWER".
        (await _sut.ActivatePendingOtpSecret(UserId, "NEW")).Should().BeFalse();

        var props = await Reload();
        props.OtpSecret.Should().Be("OLD");
        props.PendingOtpSecret.Should().Be("NEWER");
    }

    [Fact]
    public async Task Activate_NoPendingSecret_Fails()
    {
        await SeedActiveAuthenticator();

        (await _sut.ActivatePendingOtpSecret(UserId, "OLD")).Should().BeFalse();
        (await _sut.ActivatePendingOtpSecret(UserId, "ANY")).Should().BeFalse();
    }

    [Fact]
    public async Task Activate_Twice_SucceedsOnlyOnce()
    {
        await _sut.SetPendingOtpSecret(UserId, "NEW", DateTime.UtcNow.AddMinutes(10));

        (await _sut.ActivatePendingOtpSecret(UserId, "NEW")).Should().BeTrue();
        (await _sut.ActivatePendingOtpSecret(UserId, "NEW")).Should().BeFalse();
    }

    [Fact]
    public async Task DisableOtp_ClearsPendingSecret()
    {
        await SeedActiveAuthenticator();
        await _sut.SetPendingOtpSecret(UserId, "NEW", DateTime.UtcNow.AddMinutes(10));

        // Отдельный запрос со своим контекстом: ExecuteUpdate активации не обновляет сущности, уже отслеживаемые _db.Context.
        using (var ctx = _db.CreateAdditionalContext())
        {
            await new AuthPropertiesStorage(ctx).DisableOtp(UserId);
        }

        var props = await Reload();
        props.OtpEnabled.Should().BeFalse();
        props.PendingOtpSecret.Should().BeNull();
        props.PendingOtpSecretExpiresAt.Should().BeNull();
    }
}
