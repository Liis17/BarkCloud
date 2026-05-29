using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.Privacy.UpdatePrivacySettings;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

using ProtoVisibility = BarkCloud.Proto.Users.PrivacyVisibility;

namespace BarkCloud.Users.Tests.Features.Privacy.UpdatePrivacySettings;

public class UpdatePrivacySettingsCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _users = new();

    private UpdatePrivacySettingsCommandHandler CreateSut(long userId = 42) => new(
        _users.Object,
        UserContextFactory.Create(userId),
        NullLogger<UpdatePrivacySettingsCommandHandler>.Instance);

    [Fact]
    public async Task Handle_PassesAllFieldsToStorageAndReturnsMapped()
    {
        _users.Setup(s => s.UpdatePrivacy(42,
                PrivacyVisibility.Contacts, PrivacyVisibility.Nobody, PrivacyVisibility.Everyone, false))
            .ReturnsAsync(new UserPrivacy
            {
                UserId = 42,
                ProfileVisibility = PrivacyVisibility.Contacts,
                EmailVisibility = PrivacyVisibility.Nobody,
                LastSeenVisibility = PrivacyVisibility.Everyone,
                SearchableByUsername = false
            });

        var response = await CreateSut().Handle(new UpdatePrivacySettingsCommand
        {
            ProfileVisibility = PrivacyVisibility.Contacts,
            EmailVisibility = PrivacyVisibility.Nobody,
            LastSeenVisibility = PrivacyVisibility.Everyone,
            SearchableByUsername = false
        }, default);

        response.Settings.ProfileVisibility.Should().Be(ProtoVisibility.Contacts);
        response.Settings.SearchableByUsername.Should().BeFalse();
        _users.Verify(s => s.UpdatePrivacy(42,
            PrivacyVisibility.Contacts, PrivacyVisibility.Nobody, PrivacyVisibility.Everyone, false), Times.Once);
    }
}
