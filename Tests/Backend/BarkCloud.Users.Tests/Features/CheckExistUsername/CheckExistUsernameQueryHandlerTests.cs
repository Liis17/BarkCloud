using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.CheckExistUsername;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.CheckExistUsername;

public class CheckExistUsernameQueryHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();

    private CheckExistUsernameQueryHandler CreateSut(params string[] reserved) => new(
        _usersStorage.Object,
        ReservedUsernamesFactory.Create(reserved),
        NullLogger<CheckExistUsernameQueryHandler>.Instance);

    [Fact]
    public async Task Handle_ReservedUsername_ReturnsExistTrue()
    {
        var sut = CreateSut("admin");

        var response = await sut.Handle(new CheckExistUsernameQuery { Username = "Admin" }, default);

        response.Exist.Should().BeTrue();
        _usersStorage.Verify(s => s.GetUserByUsername(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NotFound_ReturnsExistFalse()
    {
        _usersStorage.Setup(s => s.GetUserByUsername("john")).ReturnsAsync((User?)null);
        var sut = CreateSut();

        var response = await sut.Handle(new CheckExistUsernameQuery { Username = "john" }, default);

        response.Exist.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_DraftUser_ReturnsExistFalse()
    {
        _usersStorage.Setup(s => s.GetUserByUsername("john"))
            .ReturnsAsync(new User { Id = 1, Username = "john", IsDraft = true });
        var sut = CreateSut();

        var response = await sut.Handle(new CheckExistUsernameQuery { Username = "john" }, default);

        response.Exist.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ActiveUser_ReturnsExistTrue()
    {
        _usersStorage.Setup(s => s.GetUserByUsername("john"))
            .ReturnsAsync(new User { Id = 1, Username = "john", IsDraft = false });
        var sut = CreateSut();

        var response = await sut.Handle(new CheckExistUsernameQuery { Username = "john" }, default);

        response.Exist.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_TrimsUsernameBeforeChecking()
    {
        _usersStorage.Setup(s => s.GetUserByUsername("john"))
            .ReturnsAsync(new User { Id = 1, Username = "john", IsDraft = false });
        var sut = CreateSut();

        var response = await sut.Handle(new CheckExistUsernameQuery { Username = "  john  " }, default);

        response.Exist.Should().BeTrue();
        _usersStorage.Verify(s => s.GetUserByUsername("john"), Times.Once);
    }
}
