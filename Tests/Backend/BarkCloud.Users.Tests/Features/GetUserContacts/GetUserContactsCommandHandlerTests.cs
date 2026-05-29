using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.GetUserContacts;
using BarkCloud.Users.Persistence.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.GetUserContacts;

public class GetUserContactsCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _users = new();

    private GetUserContactsCommandHandler CreateSut() => new(
        _users.Object,
        NullLogger<GetUserContactsCommandHandler>.Instance);

    [Fact]
    public async Task Handle_UserNotFound_Throws()
    {
        _users.Setup(s => s.GetById(7)).ReturnsAsync((User?)null);

        var act = () => CreateSut().Handle(new GetUserContactsCommand { UserId = 7 }, default);

        await act.Should().ThrowAsync<UserNotFoundException>();
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsUserAndContactEmail()
    {
        _users.Setup(s => s.GetById(7)).ReturnsAsync(new User
        {
            Id = 7,
            Username = "barker",
            FirstName = "Bark",
            LastName = "Dog",
            RegistrationDate = DateTime.UtcNow,
            Contact = new UserContact { Email = "bark@dog.io" }
        });

        var response = await CreateSut().Handle(new GetUserContactsCommand { UserId = 7 }, default);

        response.User.Id.Should().Be(7);
        response.User.Username.Should().Be("barker");
        response.Contact.Email.Should().Be("bark@dog.io");
    }
}
