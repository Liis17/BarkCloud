namespace BarkCloud.Identity.Settings;

public class JwtSettings
{
    public string Issuer { get; set; }

    public string Audience { get; set; }

    public int ExpiryMinutes { get; set; }

    /// <summary>PEM-файл приватного RSA-ключа подписи (только Identity; см. RsaJwtKeys).</summary>
    public string? PrivateKeyFile { get; set; }
}
