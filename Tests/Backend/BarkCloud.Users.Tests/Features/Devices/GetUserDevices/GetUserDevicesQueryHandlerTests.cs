using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.Devices.GetUserDevices;
using BarkCloud.Users.Persistence.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.Devices.GetUserDevices;

public class GetUserDevicesQueryHandlerTests
{
    private readonly Mock<IDevicesStorage> _devices = new();

    private GetUserDevicesQueryHandler CreateSut() => new(
        _devices.Object,
        NullLogger<GetUserDevicesQueryHandler>.Instance);

    [Fact]
    public async Task Handle_MapsDevicesForRequestedUser()
    {
        _devices.Setup(s => s.GetDevicesByUserId(99)).ReturnsAsync(new List<UserDevice>
        {
            new() { Id = Guid.NewGuid(), UserId = 99, OriginalName = "Pixel", AuthorizedAt = DateTime.UtcNow }
        });

        var response = await CreateSut().Handle(new GetUserDevicesQuery { UserId = 99 }, default);

        response.Devices.Should().ContainSingle();
        response.Devices[0].UserId.Should().Be(99);
        response.Devices[0].OriginalName.Should().Be("Pixel");
        _devices.Verify(s => s.GetDevicesByUserId(99), Times.Once);
    }
}
