using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.Devices.GetDevices;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.Devices.GetDevices;

public class GetDevicesQueryHandlerTests
{
    private readonly Mock<IDevicesStorage> _devices = new();

    private GetDevicesQueryHandler CreateSut(long userId = 42) => new(
        _devices.Object,
        UserContextFactory.Create(userId),
        NullLogger<GetDevicesQueryHandler>.Instance);

    [Fact]
    public async Task Handle_MapsAllDevicesForContextUser()
    {
        _devices.Setup(s => s.GetDevicesByUserId(42)).ReturnsAsync(new List<UserDevice>
        {
            new() { Id = Guid.NewGuid(), UserId = 42, OriginalName = "A", AuthorizedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), UserId = 42, OriginalName = "B", AuthorizedAt = DateTime.UtcNow, CustomName = null }
        });

        var response = await CreateSut().Handle(new GetDevicesQuery(), default);

        response.Devices.Should().HaveCount(2);
        response.Devices[0].OriginalName.Should().Be("A");
        response.Devices[1].CustomName.Should().Be("");
    }

    [Fact]
    public async Task Handle_NoDevices_ReturnsEmpty()
    {
        _devices.Setup(s => s.GetDevicesByUserId(42)).ReturnsAsync(new List<UserDevice>());

        var response = await CreateSut().Handle(new GetDevicesQuery(), default);

        response.Devices.Should().BeEmpty();
    }
}
