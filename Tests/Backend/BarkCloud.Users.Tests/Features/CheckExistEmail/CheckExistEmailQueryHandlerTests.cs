using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.CheckExistEmail;
using BarkCloud.Users.Persistence.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.CheckExistEmail;

public class CheckExistEmailQueryHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();

    private CheckExistEmailQueryHandler CreateSut() => new(
        _usersStorage.Object,
        NullLogger<CheckExistEmailQueryHandler>.Instance);

    [Fact]
    public async Task Handle_NotFound_ReturnsExistFalse()
    {
        _usersStorage.Setup(s => s.GetUserByEmail("a@b")).ReturnsAsync((User?)null);

        var response = await CreateSut().Handle(new CheckExistEmailQuery { Email = "a@b" }, default);

        response.Exist.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_DraftUser_ReturnsExistFalse()
    {
        _usersStorage.Setup(s => s.GetUserByEmail("a@b"))
            .ReturnsAsync(new User { Id = 1, IsDraft = true });

        var response = await CreateSut().Handle(new CheckExistEmailQuery { Email = "a@b" }, default);

        response.Exist.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ActiveUser_ReturnsExistTrue()
    {
        _usersStorage.Setup(s => s.GetUserByEmail("a@b"))
            .ReturnsAsync(new User { Id = 1, IsDraft = false });

        var response = await CreateSut().Handle(new CheckExistEmailQuery { Email = "a@b" }, default);

        response.Exist.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_TrimsEmail()
    {
        _usersStorage.Setup(s => s.GetUserByEmail("a@b"))
            .ReturnsAsync(new User { Id = 1, IsDraft = false });

        var response = await CreateSut().Handle(new CheckExistEmailQuery { Email = "  a@b  " }, default);

        response.Exist.Should().BeTrue();
        _usersStorage.Verify(s => s.GetUserByEmail("a@b"), Times.Once);
    }
}
