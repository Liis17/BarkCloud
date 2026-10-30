using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Identity.Domain;

public class ReauthPermission
{
    [Key]
    public Guid Id { get; set; }

    public long UserId { get; set; }

    public long SessionId { get; set; }

    public int Action { get; set; }

    public long? CredentialId { get; set; }

    public long SecurityStateVersion { get; set; }

    public byte[]? PermissionTokenHash { get; set; }

    public ReauthPermissionStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public Guid? WebAuthnChallengeId { get; set; }
}

public enum ReauthPermissionStatus
{
    EmailPending = 0,
    PasskeyPending = 1,
    Granted = 2,
    RegistrationPending = 3,
    Consumed = 4
}
