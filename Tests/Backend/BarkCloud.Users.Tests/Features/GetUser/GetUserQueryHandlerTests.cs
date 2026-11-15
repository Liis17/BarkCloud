using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.GetUser;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.GetUser;

public class GetUserQueryHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();

    private GetUserQueryHandler CreateSut(long requesterId = 100, TokenType tokenType = TokenType.User) => new(
        _usersStorage.Object,
        UserContextFactory.Create(requesterId, tokenType: tokenType),
        NullLogger<GetUserQueryHandler>.Instance);

    private static User ProfileUser(long id, PrivacyVisibility? profileVisibility) => new()
    {
        Id = id,
        Username = "target",
        FirstName = "First",
        LastName = "Last",
        Bio = "bio",
        ProfilePicture = "avatar.jpg",
        ProfilePicturePreviewUrl = "avatar-preview.jpg",
        RegistrationDate = DateTime.UtcNow,
        Contact = new UserContact { Email = "a@b" },
        Privacy = profileVisibility is { } visibility ? new UserPrivacy { ProfileVisibility = visibility } : null
    };

    [Theory]
    [InlineData(null)]
    [InlineData(PrivacyVisibility.Everyone)]
    [InlineData(PrivacyVisibility.Contacts)]
    [InlineData(PrivacyVisibility.Nobody)]
    public async Task Handle_OwnProfile_ReturnsFullProfileForAnyVisibility(PrivacyVisibility? visibility)
    {
        _usersStorage.Setup(s => s.GetById(100)).ReturnsAsync(ProfileUser(100, visibility));

        var response = await CreateSut(requesterId: 100).Handle(new GetUserQuery { UserId = 100 }, default);

        response.User.FirstName.Should().Be("First");
        response.User.Bio.Should().Be("bio");
        response.User.ProfilePicture.Should().Be("avatar.jpg");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PrivacyVisibility.Everyone)]
    public async Task Handle_OtherUserVisibleProfile_ReturnsFullProfile(PrivacyVisibility? visibility)
    {
        _usersStorage.Setup(s => s.GetById(7)).ReturnsAsync(ProfileUser(7, visibility));

        var response = await CreateSut(requesterId: 100).Handle(new GetUserQuery { UserId = 7 }, default);

        response.User.FirstName.Should().Be("First");
        response.User.LastName.Should().Be("Last");
        response.User.Bio.Should().Be("bio");
        response.User.ProfilePicturePreview.Should().Be("avatar-preview.jpg");
    }

    [Theory]
    [InlineData(PrivacyVisibility.Contacts)]
    [InlineData(PrivacyVisibility.Nobody)]
    public async Task Handle_OtherUserHiddenProfile_ReturnsOnlyIdAndUsername(PrivacyVisibility visibility)
    {
        _usersStorage.Setup(s => s.GetById(7)).ReturnsAsync(ProfileUser(7, visibility));

        var response = await CreateSut(requesterId: 100).Handle(new GetUserQuery { UserId = 7 }, default);

        response.User.Id.Should().Be(7);
        response.User.Username.Should().Be("target");
        response.User.FirstName.Should().BeEmpty();
        response.User.LastName.Should().BeEmpty();
        response.User.Bio.Should().BeEmpty();
        response.User.ProfilePicture.Should().BeEmpty();
        response.User.ProfilePicturePreview.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ServiceToken_ReturnsFullHiddenProfile()
    {
        _usersStorage.Setup(s => s.GetById(7)).ReturnsAsync(ProfileUser(7, PrivacyVisibility.Nobody));

        var response = await CreateSut(requesterId: 0, tokenType: TokenType.Service)
            .Handle(new GetUserQuery { UserId = 7 }, default);

        response.User.FirstName.Should().Be("First");
        response.User.ProfilePicture.Should().Be("avatar.jpg");
    }

    private static User DomainUser(long id, string username) => new()
    {
        Id = id,
        Username = username,
        FirstName = "F",
        LastName = "L",
        RegistrationDate = DateTime.UtcNow,
        Contact = new UserContact { Email = "a@b" }
    };

    [Fact]
    public async Task Handle_RequestedIdNull_FallsBackToCurrentUser()
    {
        _usersStorage.Setup(s => s.GetById(100))
            .ReturnsAsync(DomainUser(100, "me"));

        var response = await CreateSut().Handle(new GetUserQuery { UserId = null }, default);

        response.User.Id.Should().Be(100);
    }

    [Fact]
    public async Task Handle_UserNotFound_Throws()
    {
        _usersStorage.Setup(s => s.GetById(It.IsAny<long>())).ReturnsAsync((User?)null);

        var act = () => CreateSut().Handle(new GetUserQuery { UserId = 999 }, default);

        await act.Should().ThrowAsync<UserNotFoundException>();
    }

    [Fact]
    public async Task Handle_ExplicitId_ReturnsThatUser()
    {
        _usersStorage.Setup(s => s.GetById(7))
            .ReturnsAsync(DomainUser(7, "u"));

        var response = await CreateSut().Handle(new GetUserQuery { UserId = 7 }, default);

        response.User.Id.Should().Be(7);
        response.User.Username.Should().Be("u");
    }
}
