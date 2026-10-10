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
            .Setup(c => c.RegisterDeviceAsync(
                It.IsAny<RegisterDeviceRequest>(), It.IsAny<Metadata?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
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
            null, It.IsAny<DateTime?>(), default), Times.Once);
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
            .Setup(c => c.RegisterDeviceAsync(
                It.IsAny<RegisterDeviceRequest>(), It.IsAny<Metadata?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "users down")));

        var response = await CreateSut().IssueAsync(7, default);

        response.AccessToken.Value.Should().Be("access");
        _outbox.Verify(o => o.EnqueueAsync(
            7, NotificationType.SuccessfulLogin, It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IssueAsync_CallerCancellationStopsWaitingForPendingDeviceRegistration(bool fromRequest)
    {
        using var cts = new CancellationTokenSource();
        var registration = SetupPendingRegistration();
        string? storedRefreshToken = null;
        string? commandRefreshToken = null;
        _refreshTokens
            .Setup(s => s.CreateNewRefreshToken(It.IsAny<string>(), 7, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, long, string, int, CancellationToken>((token, _, _, _, _) => storedRefreshToken = token);
        _mediator
            .Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .Returns((CreateTokenCommand command, CancellationToken _) =>
            {
                commandRefreshToken = command.RefreshToken;
                return Task.FromResult(new CreateTokenResponse { AccessToken = new Token { Value = "access" } });
            });
        var sut = CreateSut();
        var issueTask = fromRequest
            ? sut.IssueAsync(7, cts.Token)
            : sut.IssueAsync(7, new SessionDevice("srv-device", "Server", "Linux", "BarkCloud.Web", "198.51.100.9"), cts.Token);

        try
        {
            await registration.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cts.Cancel();

            var act = async () => await issueTask.WaitAsync(TimeSpan.FromSeconds(2));
            var exception = await act.Should().ThrowAsync<OperationCanceledException>();

            exception.Which.CancellationToken.Should().Be(cts.Token);
            registration.CancellationToken.Should().Be(cts.Token);
            registration.CancellationToken.IsCancellationRequested.Should().BeTrue();
            registration.DisposeCount.Should().Be(1);
            storedRefreshToken.Should().Be(commandRefreshToken);
            storedRefreshToken.Should().NotBeNullOrWhiteSpace();
            _refreshTokens.Verify(s => s.CreateNewRefreshToken(
                storedRefreshToken!, 7, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
            // Письмо фиксируется в outbox вместе с сессией до регистрации устройства.
            _outbox.Verify(o => o.EnqueueAsync(
                7, NotificationType.SuccessfulLogin, It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<CancellationToken>()), Times.Once);
            var metrics = _metrics.SnapshotAndReset();
            metrics.Should().NotContainKey("sessions_created");
            metrics.Should().NotContainKey("auth_login_success");
        }
        finally
        {
            registration.Response.TrySetResult(new RegisterDeviceResponse());
            try
            {
                await issueTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Cleanup must stay bounded and preserve the original assertion failure.
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IssueAsync_PendingDeviceRegistrationTimesOutAndKeepsSavedSession(bool fromRequest)
    {
        var registration = SetupPendingRegistration();
        string? storedRefreshToken = null;
        string? commandRefreshToken = null;
        _refreshTokens
            .Setup(s => s.CreateNewRefreshToken(It.IsAny<string>(), 7, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, long, string, int, CancellationToken>((token, _, _, _, _) => storedRefreshToken = token);
        _mediator
            .Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .Returns((CreateTokenCommand command, CancellationToken _) =>
            {
                commandRefreshToken = command.RefreshToken;
                return Task.FromResult(new CreateTokenResponse { AccessToken = new Token { Value = "access" } });
            });

        var start = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var sut = CreateSut();
        var issueTask = fromRequest
            ? sut.IssueAsync(7, default)
            : sut.IssueAsync(7, new SessionDevice("srv-device", "Server", "Linux", "BarkCloud.Web", "198.51.100.9"), default);

        try
        {
            await registration.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            registration.Deadline.Should().NotBeNull();
            registration.Deadline!.Value.Kind.Should().Be(DateTimeKind.Utc);
            registration.Deadline!.Value.Should().BeOnOrAfter(start.AddSeconds(10));
            registration.Deadline.Value.Should().BeOnOrBefore(registration.RpcStartedAt!.Value.AddSeconds(10));

            var response = await issueTask.WaitAsync(TimeSpan.FromSeconds(13));
            stopwatch.Stop();

            stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(9));
            response.AccessToken.Value.Should().Be("access");
            response.RefreshToken.Value.Should().Be(storedRefreshToken);
            storedRefreshToken.Should().Be(commandRefreshToken);
            storedRefreshToken.Should().NotBeNullOrWhiteSpace();
            registration.Request.Should().NotBeNull();
            registration.Request!.DeviceId.Should().Be(fromRequest ? "device-1" : "srv-device");
            registration.CancellationToken.Should().Be(CancellationToken.None);
            registration.DisposeCount.Should().Be(1);
            _users.Verify(c => c.RegisterDeviceAsync(
                It.IsAny<RegisterDeviceRequest>(), null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
            _outbox.Verify(o => o.EnqueueAsync(
                7, NotificationType.SuccessfulLogin, It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()), Times.Once);
            var metrics = _metrics.SnapshotAndReset();
            metrics["sessions_created"].Should().Be(1);
            if (fromRequest)
            {
                metrics["auth_login_success"].Should().Be(1);
            }
            else
            {
                metrics.Should().NotContainKey("auth_login_success");
            }
        }
        finally
        {
            registration.Response.TrySetResult(new RegisterDeviceResponse());
            try
            {
                await issueTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Cleanup must stay bounded and preserve the original assertion failure.
            }
        }
    }

    [Theory]
    [InlineData(StatusCode.DeadlineExceeded, true)]
    [InlineData(StatusCode.Unavailable, true)]
    [InlineData(StatusCode.Cancelled, true)]
    [InlineData(StatusCode.DeadlineExceeded, false)]
    [InlineData(StatusCode.Unavailable, false)]
    [InlineData(StatusCode.Cancelled, false)]
    public async Task IssueAsync_RegistrationRpcFailureWithoutCallerCancellationKeepsSavedSession(
        StatusCode statusCode, bool fromRequest)
    {
        var responseTask = Task.FromException<RegisterDeviceResponse>(
            new RpcException(new Status(statusCode, "registration failed")));
        var registration = SetupPendingRegistration(responseTask: responseTask);
        string? storedRefreshToken = null;
        string? commandRefreshToken = null;
        _refreshTokens
            .Setup(s => s.CreateNewRefreshToken(It.IsAny<string>(), 7, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, long, string, int, CancellationToken>((token, _, _, _, _) => storedRefreshToken = token);
        _mediator
            .Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .Returns((CreateTokenCommand command, CancellationToken _) =>
            {
                commandRefreshToken = command.RefreshToken;
                return Task.FromResult(new CreateTokenResponse { AccessToken = new Token { Value = "access" } });
            });

        var sut = CreateSut();
        var issueTask = fromRequest
            ? sut.IssueAsync(7, default)
            : sut.IssueAsync(7, new SessionDevice("srv-device", "Server", "Linux", "BarkCloud.Web", "198.51.100.9"), default);
        await registration.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var response = await issueTask.WaitAsync(TimeSpan.FromSeconds(2));

        response.AccessToken.Value.Should().Be("access");
        response.RefreshToken.Value.Should().Be(storedRefreshToken);
        storedRefreshToken.Should().Be(commandRefreshToken);
        registration.CancellationToken.Should().Be(CancellationToken.None);
        registration.DisposeCount.Should().Be(1);
        _users.Verify(c => c.RegisterDeviceAsync(
            It.IsAny<RegisterDeviceRequest>(), null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        _outbox.Verify(o => o.EnqueueAsync(
            7, NotificationType.SuccessfulLogin, It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()), Times.Once);
        var metrics = _metrics.SnapshotAndReset();
        metrics["sessions_created"].Should().Be(1);
        if (fromRequest)
        {
            metrics["auth_login_success"].Should().Be(1);
        }
        else
        {
            metrics.Should().NotContainKey("auth_login_success");
        }
    }

    [Theory]
    [InlineData(RegistrationCompletion.CancelledException, true)]
    [InlineData(RegistrationCompletion.CancelledRpcException, true)]
    [InlineData(RegistrationCompletion.Success, true)]
    [InlineData(RegistrationCompletion.Unavailable, true)]
    [InlineData(RegistrationCompletion.CancelledException, false)]
    [InlineData(RegistrationCompletion.CancelledRpcException, false)]
    [InlineData(RegistrationCompletion.Success, false)]
    [InlineData(RegistrationCompletion.Unavailable, false)]
    public async Task IssueAsync_CallerCancelledDuringRegistrationStopsBeforeSessionMetrics(
        RegistrationCompletion completion, bool fromRequest)
    {
        using var cts = new CancellationTokenSource();
        Task<RegisterDeviceResponse> responseTask = completion switch
        {
            RegistrationCompletion.CancelledException => Task.FromException<RegisterDeviceResponse>(new OperationCanceledException()),
            RegistrationCompletion.CancelledRpcException => Task.FromException<RegisterDeviceResponse>(
                new RpcException(new Status(StatusCode.Cancelled, "cancelled"))),
            RegistrationCompletion.Success => Task.FromResult(new RegisterDeviceResponse()),
            RegistrationCompletion.Unavailable => Task.FromException<RegisterDeviceResponse>(
                new RpcException(new Status(StatusCode.Unavailable, "users down"))),
            _ => throw new ArgumentOutOfRangeException(nameof(completion))
        };
        var registration = SetupPendingRegistration(cts.Cancel, responseTask);
        var sut = CreateSut();
        var issueTask = fromRequest
            ? sut.IssueAsync(7, cts.Token)
            : sut.IssueAsync(7, new SessionDevice("srv-device", "Server", "Linux", "BarkCloud.Web", "198.51.100.9"), cts.Token);

        await registration.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var act = async () => await issueTask.WaitAsync(TimeSpan.FromSeconds(2));
        var exception = await act.Should().ThrowAsync<OperationCanceledException>();

        exception.Which.CancellationToken.Should().Be(cts.Token);
        registration.CancellationToken.Should().Be(cts.Token);
        registration.DisposeCount.Should().Be(1);
        _outbox.Verify(o => o.EnqueueAsync(
            7, NotificationType.SuccessfulLogin, It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        var metrics = _metrics.SnapshotAndReset();
        metrics.Should().NotContainKey("sessions_created");
        metrics.Should().NotContainKey("auth_login_success");
    }

    [Fact]
    public async Task IssueAsync_CallerCancelledBeforeDeviceRegistrationDoesNotStartRpc()
    {
        using var cts = new CancellationTokenSource();
        _location
            .Setup(c => c.GetLocation(It.IsAny<string>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromResult<IpLocation?>(null);
            });

        var act = async () => await CreateSut().IssueAsync(7, cts.Token).WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cts.Token);
        _users.Verify(c => c.RegisterDeviceAsync(
            It.IsAny<RegisterDeviceRequest>(), It.IsAny<Metadata?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _outbox.Verify(o => o.EnqueueAsync(
            It.IsAny<long>(), It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        var metrics = _metrics.SnapshotAndReset();
        metrics.Should().NotContainKey("sessions_created");
        metrics.Should().NotContainKey("auth_login_success");
    }

    [Fact]
    public async Task IssueAsync_ExplicitDevice_UsesItAndDoesNotCountUserLogin()
    {
        var device = new SessionDevice("srv-device", "Server", "Linux", "BarkCloud.Web", "198.51.100.9");

        await CreateSut().IssueAsync(7, device, default);

        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), 7, "srv-device", It.IsAny<int>()), Times.Once);
        _users.Verify(c => c.RegisterDeviceAsync(
            It.Is<RegisterDeviceRequest>(r => r.DeviceId == "srv-device" && r.OriginalName == "Server" && r.AppName == "BarkCloud.Web"),
            null, It.IsAny<DateTime?>(), default), Times.Once);
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

    private PendingRegistration SetupPendingRegistration(
        Action? beforeReturn = null, Task<RegisterDeviceResponse>? responseTask = null)
    {
        var registration = new PendingRegistration();
        _users
            .Setup(c => c.RegisterDeviceAsync(
                It.IsAny<RegisterDeviceRequest>(), It.IsAny<Metadata?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns((RegisterDeviceRequest request, Metadata? _, DateTime? deadline, CancellationToken cancellationToken) =>
            {
                registration.Request = request;
                registration.RpcStartedAt = DateTime.UtcNow;
                registration.Deadline = deadline;
                registration.CancellationToken = cancellationToken;
                beforeReturn?.Invoke();
                registration.Entered.TrySetResult();
                return registration.CreateCall(responseTask);
            });

        return registration;
    }

    private sealed class PendingRegistration
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<RegisterDeviceResponse> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public RegisterDeviceRequest? Request { get; set; }
        public DateTime? RpcStartedAt { get; set; }
        public DateTime? Deadline { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public AsyncUnaryCall<RegisterDeviceResponse> CreateCall(Task<RegisterDeviceResponse>? responseTask = null) => new(
            responseTask ?? Response.Task,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => Interlocked.Increment(ref _disposeCount));
    }

    public enum RegistrationCompletion
    {
        CancelledException,
        CancelledRpcException,
        Success,
        Unavailable
    }
}
