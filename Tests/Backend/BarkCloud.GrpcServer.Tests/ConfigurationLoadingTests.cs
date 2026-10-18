using BarkCloud.Proto.Configuration;
using BarkCloud.Shared.Identity;
using BarkCloud.TestKit;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Time.Testing;

using System.Threading.Channels;

namespace BarkCloud.GrpcServer.Tests;

public class ConfigurationLoadingTests
{
    private const string Address = "http://configuration.test:7003";
    private readonly Mock<ConfigurationApi.ConfigurationApiClient> _client = new(MockBehavior.Strict);
    private readonly Metadata _headers = new() { { "x-config-access-key", "test-bootstrap-key" } };
    private readonly TestClock _clock = new();
    private readonly WebApplicationBuilder _builder = WebApplication.CreateBuilder();
    private int _attempts;

    [Fact]
    public async Task ReadyImmediately_LoadsSettingsWithoutWaiting()
    {
        SetupReadiness(_ => new GetReadinessResponse { Ready = true });
        SetupConfiguration();

        await Load();

        _attempts.Should().Be(1);
        _clock.DelayCount.Should().Be(0);
        _builder.Configuration["JwtSettings:Issuer"].Should().Be("configured-issuer");
        _builder.Configuration["ConfigurationServiceAddr"].Should().Be(Address);
        _client.Verify(x => x.GetConfigurationAsync(
            It.Is<GetConfigurationRequest>(r => r.ServiceId == (int)ServiceId.Torrent),
            _headers, null, default), Times.Once);
    }

    [Fact]
    public async Task NotReadyAndTransientErrors_WaitBeforeLoadingSettings()
    {
        SetupReadiness(attempt => attempt switch
        {
            1 => new GetReadinessResponse(),
            2 => throw new RpcException(new Status(StatusCode.Unavailable, "connection refused")),
            3 => throw new RpcException(new Status(StatusCode.DeadlineExceeded, "timeout")),
            _ => new GetReadinessResponse { Ready = true }
        });
        SetupConfiguration();
        var loading = Load();

        for (var i = 0; i < 3; i++)
        {
            (await _clock.NextDelay()).Should().Be(TimeSpan.FromSeconds(5));
            _client.Verify(x => x.GetConfigurationAsync(It.IsAny<GetConfigurationRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
            _clock.Advance(TimeSpan.FromSeconds(5));
        }
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        _attempts.Should().Be(4);
        _clock.DelayCount.Should().Be(3);
        _builder.Configuration["JwtSettings:Issuer"].Should().Be("configured-issuer");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task FiveFailures_StopWithoutLoadingSettings(StatusCode? status)
    {
        SetupReadiness(_ => status.HasValue
            ? throw new RpcException(new Status(status.Value, "test-bootstrap-key"))
            : new GetReadinessResponse());
        var loading = Load();
        for (var i = 0; i < 4; i++)
        {
            (await _clock.NextDelay()).Should().Be(TimeSpan.FromSeconds(5));
            _clock.Advance(TimeSpan.FromSeconds(5));
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loading.WaitAsync(TimeSpan.FromSeconds(5)));

        _attempts.Should().Be(5);
        _clock.DelayCount.Should().Be(4);
        error.Message.Should().Contain("Torrent").And.Contain(Address).And.Contain("5")
            .And.Contain(status?.ToString() ?? "не завершил инициализацию");
        error.ToString().Should().NotContain("test-bootstrap-key");
        _builder.Configuration["ConfigurationServiceAddr"].Should().BeNull();
        _client.Verify(x => x.GetConfigurationAsync(It.IsAny<GetConfigurationRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unimplemented)]
    public async Task PermanentError_StopsImmediately(StatusCode status)
    {
        SetupReadiness(_ => throw new RpcException(new Status(status, "test-bootstrap-key")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Load());

        _attempts.Should().Be(1);
        _clock.DelayCount.Should().Be(0);
        error.Message.Should().Contain(status.ToString()).And.Contain(Address);
        error.ToString().Should().NotContain("test-bootstrap-key");
    }

    [Fact]
    public async Task ConfigurationReadFailure_IsNotRetried()
    {
        SetupReadiness(_ => new GetReadinessResponse { Ready = true });
        _client.Setup(x => x.GetConfigurationAsync(It.IsAny<GetConfigurationRequest>(),
                _headers, null, default))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "lost connection")));

        await Assert.ThrowsAsync<RpcException>(() => Load());

        _attempts.Should().Be(1);
        _clock.DelayCount.Should().Be(0);
        _client.Verify(x => x.GetConfigurationAsync(It.IsAny<GetConfigurationRequest>(),
            _headers, null, default), Times.Once);
    }

    [Fact]
    public async Task Files_LoadsProfilesAndQuotaGroupsAfterReadinessUsingSameHeaders()
    {
        SetupReadiness(_ => new GetReadinessResponse { Ready = true });
        SetupConfiguration();
        var profiles = new GetStorageProfilesResponse();
        profiles.Profiles.Add(new StorageProfileItem
        {
            ProfileId = "universal-v1", Role = "universal", Version = 1,
            ServiceUrl = "http://minio:9000", AccessKey = "access", SecretKey = "secret",
            BucketName = "cloud", Region = "us-east-1", IsActive = true
        });
        profiles.QuotaGroups.Add(new StorageQuotaGroupItem
        {
            Id = "quota", Name = "Storage", QuotaBytes = 12345,
            Buckets = { new StorageBucketRef { ServiceUrl = "http://minio:9000", BucketName = "cloud" } }
        });
        _client.Setup(x => x.GetStorageProfilesAsync(
                It.Is<GetStorageProfilesRequest>(r => !r.IncludeRevisions), _headers, null, default))
            .Returns(() => GrpcCallHelpers.AsyncUnary(profiles));

        await Load(ServiceId.Files);

        _builder.Configuration["StorageProfiles:universal-v1:SecretKey"].Should().Be("secret");
        _builder.Configuration["StorageProfiles:universal-v1:ForcePathStyle"].Should().Be("True");
        _builder.Configuration["StorageProfiles:universal-v1:Region"].Should().Be("us-east-1");
        _builder.Configuration["StorageQuotaGroups:quota:QuotaBytes"].Should().Be("12345");
        _builder.Configuration["StorageQuotaGroups:quota:Buckets:0:BucketName"].Should().Be("cloud");
    }

    [Fact]
    public async Task CancellationDuringWait_StopsPolling()
    {
        SetupReadiness(_ => new GetReadinessResponse());
        using var cancellation = new CancellationTokenSource();
        var loading = Load(cancellationToken: cancellation.Token);
        await _clock.NextDelay();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        _attempts.Should().Be(1);
    }

    [Fact]
    public async Task CancellationDuringRpc_PropagatesCancellationWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        SetupReadiness(_ =>
        {
            cancellation.Cancel();
            throw new RpcException(new Status(StatusCode.Cancelled, "cancelled"));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Load(cancellationToken: cancellation.Token));

        _attempts.Should().Be(1);
        _clock.DelayCount.Should().Be(0);
    }

    [Fact]
    public async Task HangingRpc_UsesRealGrpcDeadlineThenWaits()
    {
        using var handler = new HangingHandler();
        using var channel = GrpcChannel.ForAddress(Address, new GrpcChannelOptions { HttpHandler = handler });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loading = WebApplicationBuilderExtensions.LoadConfigurationAsync(_builder, ServiceId.Torrent,
            Address, new ConfigurationApi.ConfigurationApiClient(channel), _headers, _clock, cancellation.Token);

        (await _clock.NextDelay()).Should().Be(TimeSpan.FromSeconds(5));
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
    }

    private Task<WebApplicationBuilder> Load(ServiceId serviceId = ServiceId.Torrent,
        CancellationToken cancellationToken = default)
        => WebApplicationBuilderExtensions.LoadConfigurationAsync(_builder, serviceId, Address,
            _client.Object, _headers, _clock, cancellationToken);

    private void SetupReadiness(Func<int, GetReadinessResponse> response)
    {
        _client.Setup(x => x.GetReadinessAsync(It.IsAny<GetReadinessRequest>(), _headers,
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns((GetReadinessRequest request, Metadata headers, DateTime? deadline, CancellationToken token) =>
            {
                deadline.Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(2));
                deadline.Should().BeAfter(DateTime.UtcNow.AddSeconds(1));
                return GrpcCallHelpers.AsyncUnary(response(++_attempts));
            });
    }

    private void SetupConfiguration()
    {
        var response = new GetConfigurationResponse();
        response.Configurations.Add(new ConfigurationItem
            { Section = "JwtSettings", Key = "Issuer", Value = "configured-issuer" });
        _client.Setup(x => x.GetConfigurationAsync(It.IsAny<GetConfigurationRequest>(), _headers, null, default))
            .Returns(() =>
            {
                _attempts.Should().BeGreaterThan(0);
                return GrpcCallHelpers.AsyncUnary(response);
            });
    }

    private sealed class TestClock : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _delays = global::System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();
        public int DelayCount { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            DelayCount++;
            _delays.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task<TimeSpan> NextDelay()
            => await _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The request should be cancelled by its deadline.");
            }
            finally
            {
                Cancelled.TrySetResult();
            }
        }
    }
}
