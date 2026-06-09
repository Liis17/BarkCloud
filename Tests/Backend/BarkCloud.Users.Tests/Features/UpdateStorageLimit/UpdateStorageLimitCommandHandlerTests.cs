using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.UpdateStorageLimit;
using BarkCloud.Users.Persistence.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.UpdateStorageLimit;

public class UpdateStorageLimitCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _users = new();

    private UpdateStorageLimitCommandHandler CreateSut() => new(
        _users.Object,
        NullLogger<UpdateStorageLimitCommandHandler>.Instance);

    [Fact]
    public async Task Handle_UpdatesLimitAndReturnsMappedUser()
    {
        _users.Setup(s => s.GetById(7)).ReturnsAsync(new User
        {
            Id = 7, FirstName = "Bark", LastName = "Dog", Username = "barker",
            StorageLimitGb = 50, RegistrationDate = DateTime.UtcNow
        });

        var response = await CreateSut().Handle(new UpdateStorageLimitCommand
        {
            UserId = 7, StorageLimitGb = 50
        }, default);

        _users.Verify(s => s.UpdateStorageLimitGb(7, 50), Times.Once);
        _users.Verify(s => s.GetById(7), Times.Once);
        response.User.Id.Should().Be(7);
        response.User.StorageLimitGb.Should().Be(50);
    }

    [Fact]
    public async Task Handle_AllowsZeroLimit()
    {
        _users.Setup(s => s.GetById(7)).ReturnsAsync(new User
        {
            Id = 7, FirstName = "Bark", LastName = "Dog", Username = "barker",
            StorageLimitGb = 0, RegistrationDate = DateTime.UtcNow
        });

        var response = await CreateSut().Handle(new UpdateStorageLimitCommand
        {
            UserId = 7, StorageLimitGb = 0
        }, default);

        _users.Verify(s => s.UpdateStorageLimitGb(7, 0), Times.Once);
        response.User.StorageLimitGb.Should().Be(0);
    }
}
