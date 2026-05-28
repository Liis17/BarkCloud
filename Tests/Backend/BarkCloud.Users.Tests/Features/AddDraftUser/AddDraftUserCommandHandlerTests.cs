using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Exceptions.Users;
using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.AddDraftUser;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.AddDraftUser;

public class AddDraftUserCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();
    private readonly MetricsCollector _metrics = new();

    private AddDraftUserCommandHandler CreateSut(params string[] reserved) => new(
        _usersStorage.Object,
        ReservedUsernamesFactory.Create(reserved),
        _metrics,
        NullLogger<AddDraftUserCommandHandler>.Instance);

    private static AddDraftUserCommand Command() => new()
    {
        Username = "john",
        Email = "a@b",
        FirstName = "John",
        LastName = "Doe"
    };

    [Fact]
    public async Task Handle_EmailUsedByDraft_ThrowsUserIsDraft()
    {
        _usersStorage.Setup(s => s.GetUserByEmail("a@b"))
            .ReturnsAsync(new User { Id = 1, IsDraft = true });

        var act = () => CreateSut().Handle(Command(), default);

        await act.Should().ThrowAsync<UserIsDraftException>();
        _metrics.SnapshotAndReset()["users_email_conflicts"].Should().Be(1);
    }

    [Fact]
    public async Task Handle_EmailUsedByActiveUser_ThrowsEmailExist()
    {
        _usersStorage.Setup(s => s.GetUserByEmail("a@b"))
            .ReturnsAsync(new User { Id = 1, IsDraft = false });

        var act = () => CreateSut().Handle(Command(), default);

        await act.Should().ThrowAsync<EmailExistException>();
    }

    [Fact]
    public async Task Handle_ReservedUsername_ThrowsUsernameReserved()
    {
        _usersStorage.Setup(s => s.GetUserByEmail(It.IsAny<string>())).ReturnsAsync((User?)null);

        var act = () => CreateSut("john").Handle(Command(), default);

        await act.Should().ThrowAsync<UsernameReservedException>();
        _metrics.SnapshotAndReset()["users_reserved_username_blocked"].Should().Be(1);
    }

    [Fact]
    public async Task Handle_UsernameUsedByDraft_ThrowsUserIsDraft()
    {
        _usersStorage.Setup(s => s.GetUserByEmail(It.IsAny<string>())).ReturnsAsync((User?)null);
        _usersStorage.Setup(s => s.GetUserByUsername("john"))
            .ReturnsAsync(new User { Id = 2, IsDraft = true });

        var act = () => CreateSut().Handle(Command(), default);

        await act.Should().ThrowAsync<UserIsDraftException>();
        _metrics.SnapshotAndReset()["users_username_conflicts"].Should().Be(1);
    }

    [Fact]
    public async Task Handle_UsernameUsedByActiveUser_ThrowsUsernameExist()
    {
        _usersStorage.Setup(s => s.GetUserByEmail(It.IsAny<string>())).ReturnsAsync((User?)null);
        _usersStorage.Setup(s => s.GetUserByUsername("john"))
            .ReturnsAsync(new User { Id = 2, IsDraft = false });

        var act = () => CreateSut().Handle(Command(), default);

        await act.Should().ThrowAsync<UsernameExistException>();
    }

    [Fact]
    public async Task Handle_HappyPath_CreatesUserAndReturnsId()
    {
        _usersStorage.Setup(s => s.GetUserByEmail(It.IsAny<string>())).ReturnsAsync((User?)null);
        _usersStorage.Setup(s => s.GetUserByUsername(It.IsAny<string>())).ReturnsAsync((User?)null);
        _usersStorage.Setup(s => s.CreateUser("john", "John", "Doe", "a@b"))
            .ReturnsAsync(new User { Id = 42, Username = "john" });

        var response = await CreateSut().Handle(Command(), default);

        response.UserId.Should().Be(42);
        _usersStorage.Verify(s => s.CreateUser("john", "John", "Doe", "a@b"), Times.Once);
    }

    [Fact]
    public async Task Handle_TrimsAllInputs()
    {
        _usersStorage.Setup(s => s.GetUserByEmail(It.IsAny<string>())).ReturnsAsync((User?)null);
        _usersStorage.Setup(s => s.GetUserByUsername(It.IsAny<string>())).ReturnsAsync((User?)null);
        _usersStorage.Setup(s => s.CreateUser(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new User { Id = 1 });

        await CreateSut().Handle(new AddDraftUserCommand
        {
            Username = "  john  ",
            Email = "  a@b  ",
            FirstName = "  John ",
            LastName = " Doe "
        }, default);

        _usersStorage.Verify(s => s.CreateUser("john", "John", "Doe", "a@b"), Times.Once);
    }
}
