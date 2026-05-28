using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.ConfirmUser;
using BarkCloud.Users.Persistence.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.ConfirmUser;

public class ConfirmUserCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();

    private ConfirmUserCommandHandler CreateSut() => new(
        _usersStorage.Object,
        NullLogger<ConfirmUserCommandHandler>.Instance);

    [Fact]
    public async Task Handle_UserNotFound_ThrowsUserNotFound()
    {
        _usersStorage.Setup(s => s.GetById(42)).ReturnsAsync((User?)null);

        var act = () => CreateSut().Handle(new ConfirmUserCommand { UserId = 42 }, default);

        await act.Should().ThrowAsync<UserNotFoundException>();
        _usersStorage.Verify(s => s.ChangeDraftStatus(It.IsAny<long>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Handle_UserFound_ChangesDraftToFalse()
    {
        _usersStorage.Setup(s => s.GetById(42))
            .ReturnsAsync(new User { Id = 42, Username = "u", IsDraft = true });

        await CreateSut().Handle(new ConfirmUserCommand { UserId = 42 }, default);

        _usersStorage.Verify(s => s.ChangeDraftStatus(42, false), Times.Once);
    }
}
