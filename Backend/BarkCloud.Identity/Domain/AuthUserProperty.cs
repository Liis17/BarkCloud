using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Identity.Domain;

public class AuthUserProperty
{
    [Key]
    public long Id { get; set; }

    public long UserId { get; set; }

    public bool OtpEnabled { get; set; }

    public bool EmailOtpEnabled { get; set; }

    public string? OtpSecret { get; set; }

    public OtpType SelectedOtpType { get; set; }

    public string? LastEmailAuthCode { get; set; }

    // Случайный непубличный user handle WebAuthn (один на пользователя, общий для всех его ключей).
    // Генерится при первой привязке ключа.
    public byte[]? WebAuthnUserHandle { get; set; }
}