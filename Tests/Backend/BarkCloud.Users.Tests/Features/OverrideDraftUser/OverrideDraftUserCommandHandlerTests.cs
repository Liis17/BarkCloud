using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.OverrideDraftUser;
using BarkCloud.Users.Persistence.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.OverrideDraftUser;

public class OverrideDraftUserCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();

    private OverrideDraftUserCommandHandler CreateSut() => new(
        _usersStorage.Object,
        NullLogger<OverrideDraftUserCommandHandler>.Instance);

    private static OverrideDraftUserCommand Command() => new()
    {
        Username = "john",
        Email = "a@b",
        FirstName = "John",
        LastName = "Doe"
    };

    [Fact]
    public async Task Handle_NotFoundByEmailOrUsername_Throws()
    {
        _usersStorage.Setup(s => s.GetUserByEmail(It.IsAny<string>())).ReturnsAsync((User?)null);
        _usersStorage.Setup(s => s.GetUserByUsername(It.IsAny<string>())).ReturnsAsync((User?)null);

        var act = () => CreateSut().Handle(Command(), default);

        await act.Should().ThrowAsync<UserNotFoundException>();
    }

    [Fact]
    public async Task Handle_FoundByEmail_UpdatesUserAndReturnsId()
    {
        var existing = new User
        {
            Id = 5,
            Username = "old",
            FirstName = "OldFirst",
            LastName = "OldLast",
            ProfilePicture = "old.png",
            IsDraft = false,
            Contact = new UserContact { Email = "old@e" }
        };
        _usersStorage.Setup(s => s.GetUserByEmail("a@b")).ReturnsAsync(existing);

        var response = await CreateSut().Handle(Command(), default);

        response.UserId.Should().Be(5);
        existing.Username.Should().Be("john");
        existing.FirstName.Should().Be("John");
        existing.LastName.Should().Be("Doe");
        existing.Contact.Email.Should().Be("a@b");
        existing.ProfilePicture.Should().BeNull();
        existing.IsDraft.Should().BeTrue();
        _usersStorage.Verify(s => s.UpdateTrackedUser(existing), Times.Once);
    }

    [Fact]
    public async Task Handle_FoundByUsernameOnly_UpdatesUser()
    {
        var existing = new User
        {
            Id = 7,
            Username = "old",
            Contact = new UserContact { Email = "old@e" }
        };
        _usersStorage.Setup(s => s.GetUserByEmail(It.IsAny<string>())).ReturnsAsync((User?)null);
        _usersStorage.Setup(s => s.GetUserByUsername("john")).ReturnsAsync(existing);

        var response = await CreateSut().Handle(Command(), default);

        response.UserId.Should().Be(7);
        existing.Contact.Email.Should().Be("a@b");
    }
}
