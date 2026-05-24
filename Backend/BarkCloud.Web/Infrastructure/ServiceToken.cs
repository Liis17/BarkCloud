using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

using BarkCloud.Shared.Identity;

using Microsoft.IdentityModel.Tokens;

namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// Сервисный JWT для межсервисных (server) API. Подписывается общим секретом из
/// JwtSettings — ровно так же, как это делает Configuration-сервис. Web генерит
/// его сам, чтобы не зависеть от засева Configuration (на уже существующей БД
/// новые ключи всё равно не добавятся).
/// </summary>
public static class ServiceToken
{
    public static string Generate(IConfiguration config)
    {
        var secret = config["JwtSettings:SecretKey"];
        if (string.IsNullOrEmpty(secret))
            return string.Empty;

        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(IdentityClaims.TokenType, nameof(TokenType.Service)),
            new Claim(IdentityClaims.UserId, "0"),
            new Claim("service-name", "WebClient"),
        };

        var token = new JwtSecurityToken(
            issuer: config["JwtSettings:Issuer"],
            audience: config["JwtSettings:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddYears(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
