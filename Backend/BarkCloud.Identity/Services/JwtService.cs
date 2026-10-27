using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;

using Google.Protobuf.WellKnownTypes;

using Microsoft.IdentityModel.Tokens;

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace BarkCloud.Identity.Services;

public class JwtService(JwtSettings jwtSettings)
{
    /// <summary>Срок действия сервисного токена: короткий, права проверяются на каждом RPC.</summary>
    public static readonly TimeSpan ServiceTokenLifetime = TimeSpan.FromMinutes(10);

    // Ключ читается с диска один раз на процесс (по пути); JwtService — transient.
    private static readonly ConcurrentDictionary<string, RsaSecurityKey> SigningKeyCache = new();

    public Token GenerateUserToken(long userId, string deviceId, long sessionId)
    {
        var claims = new List<Claim>
        {
            new(IdentityClaims.UserId, userId.ToString()),
            new(IdentityClaims.TokenType, TokenType.User.ToString()),
            new(IdentityClaims.DeviceId, deviceId),
            new(IdentityClaims.SessionId, sessionId.ToString()),
        };

        var dateEnd = DateTime.UtcNow.AddMinutes(jwtSettings.ExpiryMinutes);

        var token = CreateToken(claims, dateEnd);

        return new Token { Value = token, ExpirationDate = Timestamp.FromDateTime(dateEnd) };
    }

    /// <summary>
    /// Сервисный токен для вызова target-сервиса: личность вызывающего (caller), адресат и
    /// разрешения из <see cref="ServicePermissions"/>. Клиент не может выбрать чужую личность
    /// или расширить права — их определяет Identity по зарегистрированному credential.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt) GenerateServiceToken(ServiceId caller, ServiceId target)
    {
        var permissions = ServicePermissions.For(caller);
        var claims = new List<Claim>
        {
            new(IdentityClaims.ServiceId, ((int)caller).ToString()),
            new(IdentityClaims.TokenType, TokenType.Service.ToString()),
            new(IdentityClaims.TargetService, ((int)target).ToString()),
            new(IdentityClaims.Permissions, string.Join(' ', permissions)),
        };

        var dateEnd = DateTimeOffset.UtcNow.Add(ServiceTokenLifetime);

        var token = CreateToken(claims, dateEnd.UtcDateTime);

        return (token, dateEnd);
    }

    private string CreateToken(IEnumerable<Claim> claims, DateTime expiration)
    {
        // Подпись RS256 приватным ключом, он есть только у Identity: остальные сервисы
        // проверяют токены публичным ключом и выпускать их не могут.
        var credentials = new SigningCredentials(
            GetSigningKey(jwtSettings),
            SecurityAlgorithms.RsaSha256);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiration,
            Issuer = jwtSettings.Issuer,
            Audience = jwtSettings.Audience,
            SigningCredentials = credentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);

        return tokenHandler.WriteToken(token);
    }

    private static RsaSecurityKey GetSigningKey(JwtSettings settings)
    {
        var file = settings.PrivateKeyFile
                   ?? Environment.GetEnvironmentVariable(RsaJwtKeys.PrivateKeyEnvName);

        return SigningKeyCache.GetOrAdd(file ?? string.Empty, static path => RsaJwtKeys.LoadSigningKey(path));
    }
}
