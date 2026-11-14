using BarkCloud.Web.Auth;

namespace BarkCloud.Web.Tests.Auth;

public class LoginReturnUrlTests
{
    [Theory]
    [InlineData("/files?dir=abc", "/files?dir=abc")]
    [InlineData("/settings#tags", "/settings#tags")]
    [InlineData("/files?dir=%2F%2Fevil.com", "/files?dir=%2F%2Fevil.com")]
    [InlineData("/", "/")]
    public void Resolve_KeepsLocalPath(string value, string expected)
        => LoginReturnUrl.Resolve(value).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("files")]
    [InlineData("//evil.com")]
    [InlineData("//evil.com/photos")]
    [InlineData("/\\evil.com")]
    [InlineData("/files\\..\\evil")]
    [InlineData("https://evil.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/\t/evil.com")]
    [InlineData("/\n/evil.com")]
    [InlineData("/ /evil.com")]
    [InlineData("/login")]
    [InlineData("/Login?returnUrl=%2Ffiles")]
    [InlineData("/logout")]
    [InlineData("/register/confirm")]
    [InlineData("/forgot")]
    public void Resolve_RejectsForeignOrUnsafeTargets(string? value)
        => LoginReturnUrl.Resolve(value).Should().Be(LoginReturnUrl.Default);

    [Fact]
    public void Resolve_RejectsTooLongPath()
        => LoginReturnUrl.Resolve("/" + new string('a', 3000)).Should().Be(LoginReturnUrl.Default);

    [Theory]
    [InlineData("/files?dir=a%20b", "/login?returnUrl=%2Ffiles%3Fdir%3Da%2520b")]
    [InlineData("/photos", "/login")]
    [InlineData("//evil.com", "/login")]
    public void LoginPath_AddsOnlySafeReturnUrl(string pathAndQuery, string expected)
        => LoginReturnUrl.LoginPath(pathAndQuery).Should().Be(expected);
}
