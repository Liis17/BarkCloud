using BarkCloud.Shared.Identity;

using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BarkCloud.GrpcServer.XAuth;

public class UserContext
{
    public long UserId { get; }

    public TokenType TokenType { get; }

    public string? DeviceId { get; }

    /// <summary>Момент выдачи токена (клейм <c>iat</c>). Нет клейма — <see cref="DateTime.MinValue"/>
    /// (fail-safe: при отзыве сессии такой токен считается отозванным).</summary>
    public DateTime IssuedAt { get; } = DateTime.MinValue;

    /// <summary>Идентификатор сессии (клейм <c>x-session-id</c>); нет клейма — <c>null</c>.</summary>
    public long? SessionId { get; }

    public bool IsAuthenticated => UserId != 0 && TokenType != TokenType.Unknown;

    public UserContext(IHttpContextAccessor httpContextAccessor)
    {
        var principal = httpContextAccessor.HttpContext?.User;

        if (principal?.Identity?.IsAuthenticated == true)
        {
            UserId = long.Parse(principal.FindFirst(IdentityClaims.UserId)?.Value ?? "0");

            var tokenTypeValue = principal.FindFirst(IdentityClaims.TokenType)?.Value;

            Enum.TryParse(tokenTypeValue, out TokenType type);
            TokenType = type;

            DeviceId = principal.FindFirst(IdentityClaims.DeviceId)?.Value;

            if (long.TryParse(principal.FindFirst(IdentityClaims.SessionId)?.Value, out var sessionId))
            {
                SessionId = sessionId;
            }

            if (long.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Iat)?.Value, out var iat))
            {
                IssuedAt = DateTimeOffset.FromUnixTimeSeconds(iat).UtcDateTime;
            }
        }
    }
}