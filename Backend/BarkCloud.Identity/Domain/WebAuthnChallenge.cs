using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Identity.Domain;

// Временный challenge WebAuthn между шагами begin/complete. Удаляется после использования
// или по истечении TTL.
public class WebAuthnChallenge
{
    [Key]
    public Guid Id { get; set; }

    public long UserId { get; set; }

    public WebAuthnChallengeType Type { get; set; }

    // Сериализованные CredentialCreateOptions (регистрация) или AssertionOptions (вход).
    public string OptionsJson { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public long? SessionId { get; set; }

    public long SecurityStateVersion { get; set; }

    public Guid? ReauthPermissionId { get; set; }
}

public enum WebAuthnChallengeType
{
    Registration = 0,
    Assertion = 1,
    ReauthenticationAssertion = 2
}
