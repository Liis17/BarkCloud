using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.Privacy.GetPrivacySettings;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

using ProtoVisibility = BarkCloud.Proto.Users.PrivacyVisibility;

namespace BarkCloud.Users.Tests.Features.Privacy.GetPrivacySettings;

public class GetPrivacySettingsQueryHandlerTests
{
    private readonly Mock<IUsersStorage> _users = new();

    private GetPrivacySettingsQueryHandler CreateSut(long userId = 42) => new(
        _users.Object,
        UserContextFactory.Create(userId),
        NullLogger<GetPrivacySettingsQueryHandler>.Instance);

    [Fact]
    public async Task Handle_ReturnsMappedPrivacyForContextUser()
    {
        _users.Setup(s => s.GetOrCreatePrivacy(42)).ReturnsAsync(new UserPrivacy
        {
            UserId = 42,
            ProfileVisibility = PrivacyVisibility.Contacts,
            EmailVisibility = PrivacyVisibility.Nobody,
            LastSeenVisibility = PrivacyVisibility.Everyone,
            SearchableByUsername = false
        });

        var response = await CreateSut().Handle(new GetPrivacySettingsQuery(), default);

        response.Settings.ProfileVisibility.Should().Be(ProtoVisibility.Contacts);
        response.Settings.EmailVisibility.Should().Be(ProtoVisibility.Nobody);
        response.Settings.SearchableByUsername.Should().BeFalse();
        _users.Verify(s => s.GetOrCreatePrivacy(42), Times.Once);
    }
}
