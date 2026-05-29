using BarkCloud.Users.Features.Devices.RenameDevice;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.Devices.RenameDevice;

public class RenameDeviceCommandHandlerTests
{
    private readonly Mock<IDevicesStorage> _devices = new();

    private RenameDeviceCommandHandler CreateSut(long userId = 42) => new(
        _devices.Object,
        UserContextFactory.Create(userId),
        NullLogger<RenameDeviceCommandHandler>.Instance);

    [Fact]
    public async Task Handle_RenamesDeviceForContextUser()
    {
        var deviceId = Guid.NewGuid();

        await CreateSut(userId: 5).Handle(
            new RenameDeviceCommand { DeviceId = deviceId, CustomName = "Рабочий" }, default);

        _devices.Verify(s => s.RenameDevice(deviceId, 5, "Рабочий"), Times.Once);
    }
}
