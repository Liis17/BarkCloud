using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Identity;

using Microsoft.AspNetCore.Http;

using System.Security.Claims;

namespace BarkCloud.Identity.Tests._Helpers;

internal static class UserContextFactory
{
    public static UserContext Create(long userId, string deviceId = "device-1", TokenType tokenType = TokenType.User)
    {
        var claims = new List<Claim>
        {
            new(IdentityClaims.UserId, userId.ToString()),
            new(IdentityClaims.TokenType, tokenType.ToString()),
            new(IdentityClaims.DeviceId, deviceId)
        };

        var identity = new ClaimsIdentity(claims, authenticationType: "test");
        var principal = new ClaimsPrincipal(identity);

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return new UserContext(accessor);
    }
}
