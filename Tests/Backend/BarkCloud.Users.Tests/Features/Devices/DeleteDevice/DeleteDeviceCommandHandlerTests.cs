using BarkCloud.Users.Features.Devices.DeleteDevice;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.Devices.DeleteDevice;

public class DeleteDeviceCommandHandlerTests
{
    private readonly Mock<IDevicesStorage> _devices = new();

    private DeleteDeviceCommandHandler CreateSut(long userId = 42) => new(
        _devices.Object,
        UserContextFactory.Create(userId),
        NullLogger<DeleteDeviceCommandHandler>.Instance);

    [Fact]
    public async Task Handle_DeletesOwnDevice_UsesContextUserId()
    {
        var deviceId = Guid.NewGuid();

        await CreateSut(userId: 7).Handle(new DeleteDeviceCommand { DeviceId = deviceId }, default);

        _devices.Verify(s => s.DeleteDevice(deviceId, 7), Times.Once);
    }
}
