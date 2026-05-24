using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.Privacy.UpdatePrivacySettings;

public class UpdatePrivacySettingsCommand : IRequest<UpdatePrivacySettingsResponse>
{
    public Domain.PrivacyVisibility ProfileVisibility { get; set; }

    public Domain.PrivacyVisibility EmailVisibility { get; set; }

    public Domain.PrivacyVisibility LastSeenVisibility { get; set; }

    public bool SearchableByUsername { get; set; }
}
