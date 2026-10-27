using BarkCloud.Web.Auth;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;

namespace BarkCloud.Web.Tests.Auth;

public class AdminGateTests
{
    private const string Password = "admin-pass";

    private static IConfiguration Config(string? password = Password, string? secret = "supersecret_admin_signing_key_32+")
    {
        var dict = new Dictionary<string, string?> { ["App:CookieSecure"] = "false" };
        if (password is not null) dict["App:AdminPassword"] = password;
        if (secret is not null) dict["App:AdminCookieKey"] = secret;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // Переносит выданную в Response cookie в Request другого контекста (имитация следующего запроса).
    private static void TransferCookie(HttpContext from, HttpContext to)
    {
        var parsed = SetCookieHeaderValue.Parse(from.Response.Headers.SetCookie.ToString());
        to.Request.Headers.Cookie = $"{parsed.Name}={parsed.Value}";
    }

    [Fact]
    public void Enabled_FalseWhenPasswordMissing()
        => new AdminGate(Config(password: null)).Enabled.Should().BeFalse();

    [Fact]
    public void Enabled_FalseWhenSecretMissing()
        => new AdminGate(Config(secret: null)).Enabled.Should().BeFalse();

    [Fact]
    public void Enabled_TrueWhenConfigured()
        => new AdminGate(Config()).Enabled.Should().BeTrue();

    [Fact]
    public void EnsureConfigured_AdminPasswordWithoutKey_FailsClosed()
    {
        var act = () => AdminGate.EnsureConfigured(Config(secret: null));

        act.Should().Throw<InvalidOperationException>().WithMessage("*App:AdminCookieKey*");
    }

    [Fact]
    public void EnsureConfigured_NoAdminPassword_AllowsMissingKey()
    {
        var act = () => AdminGate.EnsureConfigured(Config(password: null, secret: null));

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureConfigured_TooShortKey_FailsClosed()
    {
        var act = () => AdminGate.EnsureConfigured(Config(secret: "short"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*App:AdminCookieKey*");
    }

    [Fact]
    public void Unlock_CorrectPassword_ReturnsTrueAndAppendsCookie()
    {
        var http = new DefaultHttpContext();

        new AdminGate(Config()).Unlock(http, Password).Should().BeTrue();

        http.Response.Headers.SetCookie.ToString().Should().Contain(AdminGate.AdminCookie);
    }

    [Fact]
    public void Unlock_WrongPassword_ReturnsFalseAndNoCookie()
    {
        var http = new DefaultHttpContext();

        new AdminGate(Config()).Unlock(http, "wrong").Should().BeFalse();

        http.Response.Headers.SetCookie.ToString().Should().NotContain(AdminGate.AdminCookie);
    }

    [Fact]
    public void Unlock_WhenDisabled_ReturnsFalse()
        => new AdminGate(Config(password: null)).Unlock(new DefaultHttpContext(), Password).Should().BeFalse();

    [Fact]
    public void IsUnlocked_AfterUnlock_ReturnsTrue()
    {
        var gate = new AdminGate(Config());
        var unlockCtx = new DefaultHttpContext();
        gate.Unlock(unlockCtx, Password);

        var checkCtx = new DefaultHttpContext();
        TransferCookie(unlockCtx, checkCtx);

        gate.IsUnlocked(checkCtx).Should().BeTrue();
    }

    [Fact]
    public void IsUnlocked_TamperedSignature_ReturnsFalse()
    {
        var http = new DefaultHttpContext();
        var future = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        http.Request.Headers.Cookie = $"{AdminGate.AdminCookie}={future}.not-a-valid-signature";

        new AdminGate(Config()).IsUnlocked(http).Should().BeFalse();
    }

    [Fact]
    public void IsUnlocked_ExpiredCookie_ReturnsFalse()
    {
        var http = new DefaultHttpContext();
        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        http.Request.Headers.Cookie = $"{AdminGate.AdminCookie}={past}.whatever";

        new AdminGate(Config()).IsUnlocked(http).Should().BeFalse();
    }

    [Fact]
    public void IsUnlocked_NoCookie_ReturnsFalse()
        => new AdminGate(Config()).IsUnlocked(new DefaultHttpContext()).Should().BeFalse();

    // Не-ASCII секрет валиден для того же секрета и невалиден для другого (ключ — UTF-8 байты).
    [Fact]
    public void IsUnlocked_UnicodeSecret_ValidOnlyForSameSecret()
    {
        var unlockCtx = new DefaultHttpContext();
        new AdminGate(Config(secret: "СекретныйКлючДляПодписиАдминКуки1")).Unlock(unlockCtx, Password);
        var sameSecret = new DefaultHttpContext();
        var otherSecret = new DefaultHttpContext();
        TransferCookie(unlockCtx, sameSecret);
        TransferCookie(unlockCtx, otherSecret);

        new AdminGate(Config(secret: "СекретныйКлючДляПодписиАдминКуки1")).IsUnlocked(sameSecret).Should().BeTrue();
        new AdminGate(Config(secret: "СекретныйКлючДляПодписиАдминКуки2")).IsUnlocked(otherSecret).Should().BeFalse();
    }

    [Fact]
    public void Ctor_UnusableSecret_ThrowsConfigurationError()
    {
        var act = () => new AdminGate(Config(secret: "short"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*App:AdminCookieKey*");
    }
}
