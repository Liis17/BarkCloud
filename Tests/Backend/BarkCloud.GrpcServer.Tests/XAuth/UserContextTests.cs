using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Identity;

using Microsoft.AspNetCore.Http;

using System.Security.Claims;

namespace BarkCloud.GrpcServer.Tests.XAuth;

public class UserContextTests
{
    private static UserContext Create(params Claim[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        return new UserContext(accessor);
    }

    [Fact]
    public void IssuedAt_ReadFromIatClaim()
    {
        var issuedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        var sut = Create(
            new Claim(IdentityClaims.UserId, "42"),
            new Claim(IdentityClaims.TokenType, "User"),
            new Claim("iat", issuedAt.ToUnixTimeSeconds().ToString()));

        sut.IssuedAt.Should().Be(issuedAt.UtcDateTime);
    }

    [Fact]
    public void IssuedAt_NoIatClaim_IsMinValue()
    {
        var sut = Create(
            new Claim(IdentityClaims.UserId, "42"),
            new Claim(IdentityClaims.TokenType, "User"));

        sut.IssuedAt.Should().Be(DateTime.MinValue);
    }
}
