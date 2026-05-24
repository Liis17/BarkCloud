namespace BarkCloud.Users.Domain;

public class UserPrivacy
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public User User { get; set; }

    public PrivacyVisibility ProfileVisibility { get; set; } = PrivacyVisibility.Everyone;

    public PrivacyVisibility EmailVisibility { get; set; } = PrivacyVisibility.Nobody;

    public PrivacyVisibility LastSeenVisibility { get; set; } = PrivacyVisibility.Everyone;

    public bool SearchableByUsername { get; set; } = true;
}
