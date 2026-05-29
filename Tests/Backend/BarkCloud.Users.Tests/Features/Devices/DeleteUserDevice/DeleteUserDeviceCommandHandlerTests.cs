using BarkCloud.Users.Features.Devices.DeleteUserDevice;
using BarkCloud.Users.Persistence.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.Devices.DeleteUserDevice;

public class DeleteUserDeviceCommandHandlerTests
{
    private readonly Mock<IDevicesStorage> _devices = new();

    private DeleteUserDeviceCommandHandler CreateSut() => new(
        _devices.Object,
        NullLogger<DeleteUserDeviceCommandHandler>.Instance);

    [Fact]
    public async Task Handle_DeletesDeviceForGivenUser()
    {
        var deviceId = Guid.NewGuid();

        await CreateSut().Handle(new DeleteUserDeviceCommand { DeviceId = deviceId, UserId = 99 }, default);

        _devices.Verify(s => s.DeleteDevice(deviceId, 99), Times.Once);
    }
}
