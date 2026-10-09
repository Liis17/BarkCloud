using System.Diagnostics;
using System.Net;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.TestKit;

using Grpc.Core;

using MediatR;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Services;

public class SessionIssuerTests : IDisposable
{
    private readonly Mock<UsersServerApi.UsersServerApiClient> _users = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<INotificationOutbox> _outbox = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<LocationClient> _location;
    private readonly MetricsCollector _metrics = new();
    private readonly SqliteIdentityContext _database = new();

    public void Dispose() => _database.Dispose();

    public SessionIssuerTests()
    {
        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>()))
            .ReturnsAsync(new IpLocation { Country = "Россия", RegionName = "Москва", City = "Москва" });

        _mediator.Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTokenResponse { AccessToken = new Token { Value = "access" } });

        _users
            .Setup(c => c.RegisterDeviceAsync(It.IsAny<RegisterDeviceRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new RegisterDeviceResponse()));
    }

    private static RequestContext Context(string? deviceId = "device-1") => new()
    {
        DeviceId = deviceId, DeviceName = "Pixel", OperationSystem = "Android 14",
        AppName = "BarkCloud", AppVersion = "1.0", IpAddress = "203.0.113.7"
    };

    private SessionIssuer CreateSut(RequestContext? context = null, LocationClient? location = null) => new(
        _users.Object, _mediator.Object, _outbox.Object, _refreshTokens.Object,
        _database.Context, context ?? Context(), location ?? _location.Object, _metrics, NullLogger<SessionIssuer>.Instance);

    [Fact]
    public async Task IssueAsync_FromRequest_CreatesTokensRegistersDeviceAndEnqueuesLoginMail()
    {
        var response = await CreateSut().IssueAsync(7, default);

        response.AccessToken.Value.Should().Be("access");
        response.RefreshToken.Value.Should().NotBeNullOrWhiteSpace();
        _refreshTokens.Verify(s => s.DeleteRefreshTokensByDeviceIdSafe("device-1", 7), Times.Once);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), 7, "device-1", It.IsAny<int>()), Times.Once);
        _users.Verify(c => c.RegisterDeviceAsync(
            It.Is<RegisterDeviceRequest>(r => r.DeviceId == "device-1" && r.UserId == 7
                                              && r.AppName == "BarkCloud v.1.0" && r.Location == "Россия, Москва, Москва"),
            null, null, default), Times.Once);
        _outbox.Verify(o => o.EnqueueAsync(
            7, NotificationType.SuccessfulLogin, "Успешный вход в аккаунт",
            It.Is<Dictionary<string, string>>(p =>
                p["ip"] == "203.0.113.7" && p["devicename"] == "Pixel" && p["os"] == "Android 14"
                && p["appname"] == "BarkCloud v.1.0" && p["location"] == "Россия, Москва, Москва")), Times.Once);
        var snap = _metrics.SnapshotAndReset();
        snap["auth_login_success"].Should().Be(1);
        snap["sessions_created"].Should().Be(1);
    }

    [Fact]
    public async Task IssueAsync_NoDeviceId_GeneratesTemporaryOne()
    {
        string? deviceId = null;
        _refreshTokens
            .Setup(s => s.CreateNewRefreshToken(It.IsAny<string>(), 7, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, long, string, int, CancellationToken>((_, _, device, _, _) => deviceId = device);

        await CreateSut(Context(deviceId: null)).IssueAsync(7, default);

        Guid.TryParse(deviceId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task IssueAsync_DoesNotAskUsersForContactsOrSendMailItself()
    {
        await CreateSut().IssueAsync(7, default);

        // Контакты и отправку делает воркер outbox — выпуск сессии от них не зависит.
        _users.Verify(
            c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default), Times.Never);
    }

    [Fact]
    public async Task IssueAsync_RegisterDeviceFails_StillReturnsTokensAndEnqueuesMail()
    {
        _users
            .Setup(c => c.RegisterDeviceAsync(It.IsAny<RegisterDeviceRequest>(), null, null, default))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "users down")));

        var response = await CreateSut().IssueAsync(7, default);

        response.AccessToken.Value.Should().Be("access");
        _outbox.Verify(o => o.EnqueueAsync(
            7, NotificationType.SuccessfulLogin, It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()), Times.Once);
    }

    [Fact]
    public async Task IssueAsync_ExplicitDevice_UsesItAndDoesNotCountUserLogin()
    {
        var device = new SessionDevice("srv-device", "Server", "Linux", "BarkCloud.Web", "198.51.100.9");

        await CreateSut().IssueAsync(7, device, default);

        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), 7, "srv-device", It.IsAny<int>()), Times.Once);
        _users.Verify(c => c.RegisterDeviceAsync(
            It.Is<RegisterDeviceRequest>(r => r.DeviceId == "srv-device" && r.OriginalName == "Server" && r.AppName == "BarkCloud.Web"),
            null, null, default), Times.Once);
        var snap = _metrics.SnapshotAndReset();
        snap["sessions_created"].Should().Be(1);
        snap.Should().NotContainKey("auth_login_success");
    }

    [Fact]
    public async Task IssueAsync_GeolocationHangs_DoesNotDelayLoginUntilNetworkTimeout()
    {
        var hanging = new LocationClient(
            new HttpClient(new HangingHandler()), new MetricsCollector(), NullLogger<LocationClient>.Instance);

        var stopwatch = Stopwatch.StartNew();
        var response = await CreateSut(location: hanging).IssueAsync(7, default);
        stopwatch.Stop();

        response.AccessToken.Value.Should().Be("access");
        stopwatch.Elapsed.Should().BeLessThan(LocationClient.RequestTimeout + TimeSpan.FromSeconds(3));
        _outbox.Verify(o => o.EnqueueAsync(
            7, NotificationType.SuccessfulLogin, It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(p => p["location"] == "-")), Times.Once);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
