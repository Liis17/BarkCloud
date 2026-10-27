using BarkCloud.Shared.Identity;

using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;

namespace BarkCloud.TestKit;

/// <summary>
/// Тестовая пара RSA-ключей JWT: приватный/публичный PEM во временной папке + выпуск токенов
/// так же, как это делает Identity.JwtService (RS256, kid по RsaJwtKeys.ComputeKid).
/// </summary>
public sealed class RsaJwtTestKeys : IDisposable
{
    public const string TestIssuer = "bark-issuer";
    public const string TestAudience = "bark-audience";

    private readonly RSA _rsa;
    private readonly string _directory;

    public RsaJwtTestKeys(int keyBits = 3072)
    {
        _rsa = RSA.Create(keyBits);
        _directory = Path.Combine(Path.GetTempPath(), "barkcloud-jwt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        PrivateKeyFile = Path.Combine(_directory, "jwt-private.pem");
        PublicKeyFile = Path.Combine(_directory, "jwt-public.pem");
        File.WriteAllText(PrivateKeyFile, _rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(PublicKeyFile, _rsa.ExportSubjectPublicKeyInfoPem());
    }

    public string PrivateKeyFile { get; }

    public string PublicKeyFile { get; }

    /// <summary>Тестовый RSA для ручной сборки SigningCredentials (например, с нестандартным alg).</summary>
    public RSA SigningRsa => _rsa;

    public SigningCredentials CreateSigningCredentials(string algorithm = SecurityAlgorithms.RsaSha256)
        => new(new RsaSecurityKey(_rsa) { KeyId = Kid }, algorithm);

    public string Kid => RsaJwtKeys.ComputeKid(_rsa);

    /// <summary>Подписывает JWT RS256 тестовым приватным ключом.</summary>
    public string IssueToken(
        IEnumerable<Claim> claims,
        DateTime? expires = null,
        string issuer = TestIssuer,
        string audience = TestAudience,
        SigningCredentials? credentials = null)
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = Kid };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = credentials ?? new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    /// <summary>Клеймы пользовательского токена (как Identity.JwtService.GenerateUserToken).</summary>
    public static IEnumerable<Claim> UserClaims(long userId = 1, string deviceId = "device-1", long sessionId = 5)
    {
        yield return new Claim(IdentityClaims.UserId, userId.ToString());
        yield return new Claim(IdentityClaims.TokenType, TokenType.User.ToString());
        yield return new Claim(IdentityClaims.DeviceId, deviceId);
        yield return new Claim(IdentityClaims.SessionId, sessionId.ToString());
    }

    /// <summary>Клеймы сервисного токена (как Identity.JwtService.GenerateServiceToken).</summary>
    public static IEnumerable<Claim> ServiceClaims(ServiceId caller, ServiceId target, string permissions)
    {
        yield return new Claim(IdentityClaims.ServiceId, ((int)caller).ToString());
        yield return new Claim(IdentityClaims.TokenType, TokenType.Service.ToString());
        yield return new Claim(IdentityClaims.TargetService, ((int)target).ToString());
        yield return new Claim(IdentityClaims.Permissions, permissions);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка в TEMP: неудачная очистка не должна ронять тесты.
        }

        _rsa.Dispose();
    }
}
