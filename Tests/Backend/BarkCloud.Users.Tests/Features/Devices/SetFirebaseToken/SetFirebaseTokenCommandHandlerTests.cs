using BarkCloud.Users.Features.Devices.SetFirebaseToken;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.Devices.SetFirebaseToken;

public class SetFirebaseTokenCommandHandlerTests
{
    private readonly Mock<IDevicesStorage> _devices = new();

    private SetFirebaseTokenCommandHandler CreateSut(string deviceId) => new(
        _devices.Object,
        UserContextFactory.Create(42, deviceId: deviceId),
        NullLogger<SetFirebaseTokenCommandHandler>.Instance);

    [Fact]
    public async Task Handle_DeviceIdNotAGuid_Throws()
    {
        var act = () => CreateSut(deviceId: "not-a-guid")
            .Handle(new SetFirebaseTokenCommand { FirebaseToken = "t" }, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _devices.Verify(s => s.SetFirebaseToken(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmptyToken_StoresNull()
    {
        var id = Guid.NewGuid();

        await CreateSut(deviceId: id.ToString())
            .Handle(new SetFirebaseTokenCommand { FirebaseToken = "" }, default);

        _devices.Verify(s => s.SetFirebaseToken(id, 42, null), Times.Once);
    }

    [Fact]
    public async Task Handle_NonEmptyToken_StoresToken()
    {
        var id = Guid.NewGuid();

        await CreateSut(deviceId: id.ToString())
            .Handle(new SetFirebaseTokenCommand { FirebaseToken = "fcm-123" }, default);

        _devices.Verify(s => s.SetFirebaseToken(id, 42, "fcm-123"), Times.Once);
    }
}
