using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Identity.Domain;

// Привязанный ключ безопасности (FIDO2/WebAuthn) пользователя.
public class WebAuthnCredential
{
    [Key]
    public long Id { get; set; }

    public long UserId { get; set; }

    // Идентификатор credential'а (raw id из authenticator'а).
    public byte[] CredentialId { get; set; } = [];

    // Публичный ключ (COSE), которым проверяется подпись assertion.
    public byte[] PublicKey { get; set; } = [];

    // Счётчик подписей (защита от клонирования). uint в спецификации — храним long.
    public long SignatureCounter { get; set; }

    public Guid AaGuid { get; set; }

    // Формат attestation ("none", "packed", ...).
    public string? CredType { get; set; }

    // Отображаемое имя ключа ("Мой YubiKey").
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }
}
