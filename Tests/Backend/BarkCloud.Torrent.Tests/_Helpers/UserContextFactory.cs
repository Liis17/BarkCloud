using System.Security.Claims;

using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Identity;

using Microsoft.AspNetCore.Http;

namespace BarkCloud.Torrent.Tests._Helpers;

internal static class UserContextFactory
{
    public static UserContext Create(long userId)
    {
        var claims = new List<Claim>
        {
            new(IdentityClaims.UserId, userId.ToString()),
            new(IdentityClaims.TokenType, TokenType.User.ToString()),
            new(IdentityClaims.DeviceId, "device-1"),
        };

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };

        return new UserContext(accessor);
    }
}
