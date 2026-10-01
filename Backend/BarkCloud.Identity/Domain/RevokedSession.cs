namespace BarkCloud.Identity.Domain;

public class RevokedSession
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public string DeviceId { get; set; } = null!;

    public DateTime RevokedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}
