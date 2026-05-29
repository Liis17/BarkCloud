using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.GetActiveSessionsServer;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.TestKit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.GetActiveSessionsServer;

public class GetActiveSessionsServerCommandHandlerTests
{
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();

    private GetActiveSessionsServerCommandHandler CreateSut() => new(
        _refreshTokens.Object,
        _usersClient.Object,
        NullLogger<GetActiveSessionsServerCommandHandler>.Instance);

    [Fact]
    public async Task Handle_NoSessions_ReturnsEmpty()
    {
        _refreshTokens.Setup(s => s.GetRefreshTokens(7)).ReturnsAsync(new List<RefreshToken>());
        _usersClient
            .Setup(c => c.GetUserDevicesAsync(It.IsAny<GetUserDevicesRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserDevicesResponse()));

        var response = await CreateSut().Handle(new GetActiveSessionsServerCommand { UserId = 7 }, default);

        response.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_SessionWithMatchingDevice_EnrichesSessionData()
    {
        _refreshTokens.Setup(s => s.GetRefreshTokens(7)).ReturnsAsync(new List<RefreshToken>
        {
            new() { Id = 1, DeviceId = "d1", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) }
        });
        _usersClient
            .Setup(c => c.GetUserDevicesAsync(It.IsAny<GetUserDevicesRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserDevicesResponse
            {
                Devices = { new Device { DeviceId = "d1", OriginalName = "Pixel", CustomName = "Phone", OperationSystem = "Android" } }
            }));

        var response = await CreateSut().Handle(new GetActiveSessionsServerCommand { UserId = 7 }, default);

        response.Sessions.Should().ContainSingle();
        var session = response.Sessions[0];
        session.DeviceId.Should().Be("d1");
        session.OriginalName.Should().Be("Pixel");
        session.CustomName.Should().Be("Phone");
    }

    [Fact]
    public async Task Handle_UsersClientFails_ReturnsSessionsWithoutDeviceData()
    {
        _refreshTokens.Setup(s => s.GetRefreshTokens(7)).ReturnsAsync(new List<RefreshToken>
        {
            new() { Id = 1, DeviceId = "d1", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) }
        });
        _usersClient
            .Setup(c => c.GetUserDevicesAsync(It.IsAny<GetUserDevicesRequest>(), null, null, default))
            .Throws(new InvalidOperationException("users down"));

        var response = await CreateSut().Handle(new GetActiveSessionsServerCommand { UserId = 7 }, default);

        response.Sessions.Should().ContainSingle();
        response.Sessions[0].OriginalName.Should().BeEmpty();
    }
}
