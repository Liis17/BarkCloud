using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.GetUser;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.GetUser;

public class GetUserQueryHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();

    private GetUserQueryHandler CreateSut(long requesterId = 100) => new(
        _usersStorage.Object,
        UserContextFactory.Create(requesterId),
        NullLogger<GetUserQueryHandler>.Instance);

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
