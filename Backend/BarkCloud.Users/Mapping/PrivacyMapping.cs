using BarkCloud.Proto.Users;

namespace BarkCloud.Users.Mapping;

public static class PrivacyMapping
{
    public static PrivacySettings ToGrpc(this Domain.UserPrivacy privacy)
    {
        return new PrivacySettings
        {
            ProfileVisibility = (PrivacyVisibility)privacy.ProfileVisibility,
            EmailVisibility = (PrivacyVisibility)privacy.EmailVisibility,
            LastSeenVisibility = (PrivacyVisibility)privacy.LastSeenVisibility,
            SearchableByUsername = privacy.SearchableByUsername,
        };
    }
}
