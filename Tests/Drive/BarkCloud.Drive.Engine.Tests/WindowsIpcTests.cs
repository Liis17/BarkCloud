using System.IO.Pipes;

using BarkCloud.Drive.Contracts;
using BarkCloud.Drive.Engine;

using FluentAssertions;
using Moq;
using StreamJsonRpc;
using Xunit;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class WindowsIpcTests
{
    [Fact]
    [Trait("Category", "WindowsIpc")]
    public async Task Named_pipe_completes_call_and_shutdown_response_before_server_stops()
    {
        using var lifetime = new CancellationTokenSource();
        var engine = new Mock<IDriveEngine>();
        engine.Setup(target => target.SetLanguageAsync("en")).Returns(Task.CompletedTask);
        engine.Setup(target => target.ShutdownAsync()).Returns(() =>
        {
            lifetime.Cancel();
            return Task.CompletedTask;
        });
        var pipeName = $"{EnginePipe.Name}.{Guid.NewGuid():N}";
        var server = EnginePipeServer.RunAsync(pipeName, engine.Object, lifetime.Token);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, EnginePipe.Options);

        try
        {
            await pipe.ConnectAsync(5000);
            var proxy = JsonRpc.Attach<IDriveEngine>(pipe);
            try
            {
                await proxy.SetLanguageAsync("en").WaitAsync(TimeSpan.FromSeconds(5));
                await proxy.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
                engine.Verify(target => target.SetLanguageAsync("en"), Times.Once);
                engine.Verify(target => target.ShutdownAsync(), Times.Once);
            }
            finally
            {
                ((IDisposable)proxy).Dispose();
            }
        }
        finally
        {
            lifetime.Cancel();
            await pipe.DisposeAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(1.5));
        }
    }

    [Fact]
    [Trait("Category", "WindowsIpc")]
    public async Task Named_pipe_returns_engine_status_with_s3_groups_and_stats()
    {
        using var lifetime = new CancellationTokenSource();
        var updatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var engine = new Mock<IDriveEngine>();
        engine.Setup(target => target.GetStatusAsync()).ReturnsAsync(new EngineStatus
        {
            Authenticated = true,
            DiskTotalBytes = 115_000_000_000,
            DiskOtherBytes = 10_000_000_000,
            DiskS3Bytes = 5_000_000_000,
            PhysicalStatsState = "refreshing",
            PhysicalStatsUpdatedAt = updatedAt,
            AllS3StatsAvailable = true,
            AllS3StatsState = "ready",
            AllS3StatsUpdatedAt = updatedAt,
            StorageStatsError = null,
            S3Groups =
            [
                new S3GroupStatus
                {
                    Id = "g1", Name = "Внешний S3", UsedBytes = 100, QuotaBytes = 1000,
                    IsUngrouped = false, UnmeasuredBuckets = 2,
                },
                new S3GroupStatus { Id = "ung", Name = "", UsedBytes = 50, IsUngrouped = true },
                new S3GroupStatus { Id = "g2", Name = "Unlimited", UsedBytes = 20 },
            ],
        });
        var pipeName = $"{EnginePipe.Name}.{Guid.NewGuid():N}";
        var server = EnginePipeServer.RunAsync(pipeName, engine.Object, lifetime.Token);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, EnginePipe.Options);

        try
        {
            await pipe.ConnectAsync(5000);
            var proxy = JsonRpc.Attach<IDriveEngine>(pipe);
            try
            {
                var status = await proxy.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));

                status.PhysicalStatsState.Should().Be("refreshing");
                status.PhysicalStatsUpdatedAt.Should().Be(updatedAt);
                status.AllS3StatsAvailable.Should().BeTrue();
                status.AllS3StatsUpdatedAt.Should().Be(updatedAt);
                status.S3Groups.Should().BeEquivalentTo(
                [
                    new S3GroupStatus
                    {
                        Id = "g1", Name = "Внешний S3", UsedBytes = 100, QuotaBytes = 1000,
                        IsUngrouped = false, UnmeasuredBuckets = 2,
                    },
                    new S3GroupStatus { Id = "ung", Name = "", UsedBytes = 50, IsUngrouped = true },
                    new S3GroupStatus { Id = "g2", Name = "Unlimited", UsedBytes = 20 },
                ], options => options.WithStrictOrdering());
                engine.Verify(target => target.GetStatusAsync(), Times.Once);
            }
            finally
            {
                ((IDisposable)proxy).Dispose();
            }
        }
        finally
        {
            lifetime.Cancel();
            await pipe.DisposeAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(1.5));
        }
    }
}
