using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.Devices.GetCurrentDevice;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.Devices.GetCurrentDevice;

public class GetCurrentDeviceQueryHandlerTests
{
    private readonly Mock<IDevicesStorage> _devices = new();

    private GetCurrentDeviceQueryHandler CreateSut(string deviceId) => new(
        _devices.Object,
        UserContextFactory.Create(42, deviceId: deviceId),
        NullLogger<GetCurrentDeviceQueryHandler>.Instance);

    [Fact]
    public async Task Handle_DeviceIdNotAGuid_ReturnsEmptyWithoutStorageCall()
    {
        var response = await CreateSut(deviceId: "not-a-guid").Handle(new GetCurrentDeviceQuery(), default);

        response.Device.Should().BeNull();
        _devices.Verify(s => s.GetDeviceById(It.IsAny<Guid>(), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DeviceNotFound_ReturnsEmpty()
    {
        var id = Guid.NewGuid();
        _devices.Setup(s => s.GetDeviceById(id, 42)).ReturnsAsync((UserDevice?)null);

        var response = await CreateSut(deviceId: id.ToString()).Handle(new GetCurrentDeviceQuery(), default);

        response.Device.Should().BeNull();
    }

    [Fact]
    public async Task Handle_DeviceFound_MapsFields()
    {
        var id = Guid.NewGuid();
        _devices.Setup(s => s.GetDeviceById(id, 42)).ReturnsAsync(new UserDevice
        {
            Id = id,
            UserId = 42,
            OriginalName = "iPhone",
            CustomName = "Личный",
            AuthorizedAt = DateTime.UtcNow,
            AppName = "BarkCloud",
            OperationSystem = "iOS",
            Location = "Tallinn"
        });

        var response = await CreateSut(deviceId: id.ToString()).Handle(new GetCurrentDeviceQuery(), default);

        response.Device.Should().NotBeNull();
        response.Device.DeviceId.Should().Be(id.ToString());
        response.Device.OriginalName.Should().Be("iPhone");
        response.Device.CustomName.Should().Be("Личный");
        response.Device.AppName.Should().Be("BarkCloud");
    }
}
