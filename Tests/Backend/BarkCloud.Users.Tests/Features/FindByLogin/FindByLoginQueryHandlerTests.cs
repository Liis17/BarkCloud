using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.FindByLogin;
using BarkCloud.Users.Persistence.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.FindByLogin;

public class FindByLoginQueryHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();

    private FindByLoginQueryHandler CreateSut() => new(
        _usersStorage.Object,
        NullLogger<FindByLoginQueryHandler>.Instance);

    [Fact]
    public async Task Handle_NotFoundByUsernameOrEmail_Throws()
    {
        _usersStorage.Setup(s => s.GetUserByUsername(It.IsAny<string>())).ReturnsAsync((User?)null);
        _usersStorage.Setup(s => s.GetUserByEmail(It.IsAny<string>())).ReturnsAsync((User?)null);

        var act = () => CreateSut().Handle(new FindByLoginQuery { Username = "u", Email = "" }, default);

        await act.Should().ThrowAsync<UserNotFoundException>();
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
    public async Task Handle_FoundByUsername_ReturnsUser()
    {
        _usersStorage.Setup(s => s.GetUserByUsername("john"))
            .ReturnsAsync(DomainUser(1, "john"));

        var response = await CreateSut().Handle(
            new FindByLoginQuery { Username = "john", Email = "" }, default);

        response.User.Id.Should().Be(1);
        response.User.Username.Should().Be("john");
    }

    [Fact]
    public async Task Handle_FoundByEmail_ReturnsUser()
    {
        _usersStorage.Setup(s => s.GetUserByEmail("a@b"))
            .ReturnsAsync(DomainUser(7, "jane"));

        var response = await CreateSut().Handle(
            new FindByLoginQuery { Username = "", Email = "a@b" }, default);

        response.User.Id.Should().Be(7);
    }
}
