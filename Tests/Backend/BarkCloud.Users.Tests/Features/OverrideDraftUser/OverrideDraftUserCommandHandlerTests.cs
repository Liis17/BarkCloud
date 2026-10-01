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
    public async Task Handle_ConfirmedUserAtEmail_ThrowsEmailExistAndPreservesProfile()
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

        var act = () => CreateSut().Handle(Command(), default);

        await act.Should().ThrowAsync<EmailExistException>();
        existing.Username.Should().Be("old");
        existing.Contact.Email.Should().Be("old@e");
        existing.IsDraft.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_DraftAtUsernameWithDifferentEmail_ThrowsUserNotFound()
    {
        var existing = new User
        {
            Id = 7,
            Username = "old",
            IsDraft = true,
            Contact = new UserContact { Email = "old@e" }
        };
        _usersStorage.Setup(s => s.GetUserByEmail(It.IsAny<string>())).ReturnsAsync((User?)null);
        _usersStorage.Setup(s => s.GetUserByUsername("john")).ReturnsAsync(existing);

        var act = () => CreateSut().Handle(Command(), default);

        await act.Should().ThrowAsync<UserNotFoundException>();
        existing.Contact.Email.Should().Be("old@e");
    }

    [Fact]
    public async Task Handle_DraftAtSameEmail_ReturnsSameUserId()
    {
        _usersStorage.Setup(s => s.GetUserByEmail("a@b"))
            .ReturnsAsync(new User { Id = 5, IsDraft = true });

        var response = await CreateSut().Handle(Command(), default);

        response.UserId.Should().Be(5);
        _usersStorage.Verify(s => s.OverrideDraftUser(5, "john", "John", "Doe", "a@b"), Times.Once);
    }

    [Fact]
    public async Task Handle_UsernameOwnedByAnotherUser_RejectsDraftUpdate()
    {
        _usersStorage.Setup(s => s.GetUserByEmail("a@b"))
            .ReturnsAsync(new User { Id = 5, IsDraft = true });
        _usersStorage.Setup(s => s.GetUserByUsername("john"))
            .ReturnsAsync(new User { Id = 7, IsDraft = true });

        var act = () => CreateSut().Handle(Command(), default);

        await act.Should().ThrowAsync<UsernameExistException>();
        _usersStorage.Verify(s => s.OverrideDraftUser(It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
