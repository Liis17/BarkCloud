using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.SearchUsers;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.SearchUsers;

public class SearchUsersQueryHandlerTests
{
    private readonly Mock<IUsersStorage> _users = new();

    private SearchUsersQueryHandler CreateSut(long userId = 42) => new(
        _users.Object,
        UserContextFactory.Create(userId),
        NullLogger<SearchUsersQueryHandler>.Instance);

    private static User MakeUser(long id) => new()
    {
        Id = id, Username = $"u{id}", FirstName = "F", LastName = "L", RegistrationDate = DateTime.UtcNow
    };

    [Theory]
    [InlineData("a")]
    [InlineData(" ")]
    [InlineData("")]
    public async Task Handle_QueryTooShort_ReturnsEmptyWithoutStorageCall(string query)
    {
        var response = await CreateSut().Handle(new SearchUsersQuery { Query = query, Limit = 10 }, default);

        response.Users.Should().BeEmpty();
        _users.Verify(s => s.SearchUsers(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Handle_TrimsQueryAndExcludesSelf()
    {
        _users.Setup(s => s.SearchUsers("bark", 42, It.IsAny<int>())).ReturnsAsync(new List<User> { MakeUser(1) });

        var response = await CreateSut(userId: 42).Handle(new SearchUsersQuery { Query = "  bark  ", Limit = 10 }, default);

        response.Users.Should().ContainSingle();
        _users.Verify(s => s.SearchUsers("bark", 42, 10), Times.Once);
    }

    [Fact]
    public async Task Handle_HiddenProfile_ReturnsOnlyIdAndUsername()
    {
        var hidden = MakeUser(1);
        hidden.Bio = "bio";
        hidden.ProfilePicturePreviewUrl = "preview.jpg";
        hidden.Privacy = new UserPrivacy { ProfileVisibility = PrivacyVisibility.Nobody };
        _users.Setup(s => s.SearchUsers("u1", 42, It.IsAny<int>())).ReturnsAsync(new List<User> { hidden, MakeUser(2) });

        var response = await CreateSut(userId: 42).Handle(new SearchUsersQuery { Query = "u1", Limit = 10 }, default);

        response.Users[0].Username.Should().Be("u1");
        response.Users[0].FirstName.Should().BeEmpty();
        response.Users[0].LastName.Should().BeEmpty();
        response.Users[0].Bio.Should().BeEmpty();
        response.Users[0].ProfilePicturePreview.Should().BeEmpty();
        response.Users[1].FirstName.Should().Be("F");
    }

    [Fact]
    public async Task Handle_LimitClampedToMax()
    {
        _users.Setup(s => s.SearchUsers(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(new List<User>());

        await CreateSut().Handle(new SearchUsersQuery { Query = "bark", Limit = 999 }, default);

        _users.Verify(s => s.SearchUsers("bark", 42, 50), Times.Once);
    }

    [Fact]
    public async Task Handle_NonPositiveLimit_UsesDefault()
    {
        _users.Setup(s => s.SearchUsers(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(new List<User>());

        await CreateSut().Handle(new SearchUsersQuery { Query = "bark", Limit = 0 }, default);

        _users.Verify(s => s.SearchUsers("bark", 42, 20), Times.Once);
    }
}
