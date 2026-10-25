using BarkCloud.Drive.Contracts;
using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;

using FluentAssertions;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.Drive.Engine.Tests;

// Маппинг снимка статистики в EngineStatus: данные, ошибки обновления,
// восстановление и изоляция снимка при смене аккаунта.
public sealed class DriveEngineStorageStatusTests
{
    [Fact]
    public async Task Status_reports_s3_groups_states_and_keeps_stale_data_on_refresh_failure()
    {
        await using var harness = new Harness();
        var updatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var storageCalls = 0;
        harness.Cloud.Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, _ =>
            {
                var call = Interlocked.Increment(ref storageCalls);
                if (call == 2)
                    throw new RpcException(new Status(StatusCode.Unavailable, "offline"));
                return new GetUserStorageInfoResponse
                {
                    StorageLimit = 0,
                    TotalAvailableStorage = 1000,
                    DiskUsedStorage = 100,
                    S3UsedStorage = 200,
                    PhysicalStatsState = "ready",
                    PhysicalStatsUpdatedAt = Timestamp.FromDateTime(updatedAt),
                    AllS3StatsAvailable = true,
                    AllS3StatsState = "ready",
                    AllS3StatsUpdatedAt = Timestamp.FromDateTime(updatedAt),
                    S3QuotaGroups =
                    {
                        new S3QuotaGroupUsage
                        {
                            Id = "g1", Name = "External", UsedBytes = 100, QuotaBytes = 1000,
                            UnmeasuredBuckets = 2,
                        },
                        new S3QuotaGroupUsage { Id = "ung", Name = "", UsedBytes = 50, IsUngrouped = true },
                    },
                };
            });
        await harness.LoginAsync();

        var fresh = await harness.Engine.GetStatusAsync();
        storageCalls.Should().Be(1);
        fresh.DiskTotalBytes.Should().Be(1000);
        fresh.DiskOtherBytes.Should().Be(100);
        fresh.DiskS3Bytes.Should().Be(200);
        fresh.PhysicalStatsState.Should().Be("ready");
        fresh.PhysicalStatsUpdatedAt.Should().Be(updatedAt);
        fresh.AllS3StatsAvailable.Should().BeTrue();
        fresh.AllS3StatsState.Should().Be("ready");
        fresh.AllS3StatsUpdatedAt.Should().Be(updatedAt);
        fresh.StorageStatsError.Should().BeNull();
        fresh.S3Groups.Should().BeEquivalentTo(
        [
            new S3GroupStatus
            {
                Id = "g1", Name = "External", UsedBytes = 100, QuotaBytes = 1000,
                UnmeasuredBuckets = 2,
            },
            new S3GroupStatus { Id = "ung", Name = "", UsedBytes = 50, IsUngrouped = true },
        ], options => options.WithStrictOrdering());

        // TTL кеша истёк, обновление не удалось: цифры остаются, ошибка передаётся отдельно.
        harness.Time.Advance(TimeSpan.FromSeconds(11));
        var stale = await harness.Engine.GetStatusAsync();
        storageCalls.Should().Be(2);
        stale.DiskTotalBytes.Should().Be(1000);
        stale.S3Groups.Should().HaveCount(2);
        stale.StorageStatsError.Should().Be("offline");

        // Окно негативного кеша: нового RPC нет, данные и ошибка те же.
        var negativeCached = await harness.Engine.GetStatusAsync();
        storageCalls.Should().Be(2);
        negativeCached.DiskTotalBytes.Should().Be(1000);
        negativeCached.StorageStatsError.Should().Be("offline");

        // Восстановление: ошибка снята, свежие данные.
        harness.Time.Advance(TimeSpan.FromSeconds(11));
        var recovered = await harness.Engine.GetStatusAsync();
        storageCalls.Should().Be(3);
        recovered.DiskTotalBytes.Should().Be(1000);
        recovered.StorageStatsError.Should().BeNull();
    }

    [Fact]
    public async Task Status_reports_error_without_snapshot_and_switches_to_new_account_data()
    {
        await using var harness = new Harness();
        var storageCalls = 0;
        harness.Cloud.Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, _ =>
            {
                var call = Interlocked.Increment(ref storageCalls);
                return call switch
                {
                    1 => throw new RpcException(new Status(StatusCode.Unavailable, "offline")),
                    _ => new GetUserStorageInfoResponse
                    {
                        TotalAvailableStorage = 500,
                        AllS3StatsAvailable = true,
                        S3QuotaGroups = { new S3QuotaGroupUsage { Id = "new", Name = "New" } },
                    },
                };
            });

        // Первый опрос после входа: снимка нет — ошибка без нулевых цифр.
        (await harness.LoginAsync()).StorageStatsError.Should().Be("offline");
        var noSnapshot = await harness.Engine.GetStatusAsync();
        storageCalls.Should().Be(1);
        noSnapshot.DiskTotalBytes.Should().Be(0);
        noSnapshot.S3Groups.Should().BeNull();
        noSnapshot.StorageStatsError.Should().Be("offline");

        // Повторный вход (смена сессии) чистит кеш: статус показывает данные нового аккаунта.
        var switched = await harness.Engine.LoginAsync("second-user", "password", null);
        switched.Error.Should().BeNull();
        switched.DiskTotalBytes.Should().Be(500);
        switched.S3Groups.Should().ContainSingle()
            .Which.Name.Should().Be("New");
        switched.StorageStatsError.Should().BeNull();
    }

    private sealed class Harness : IAsyncDisposable
    {
        public TestDirectory Temp { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public FakeCloud Cloud { get; } = new();
        public DriveEngine Engine { get; }

        public Harness()
        {
            var settings = new EngineSettingsStore(Temp.Path("settings"));

            var identityInvoker = new FakeCallInvoker();
            identityInvoker.Handle<AuthRequest, AuthResponse>(
                "/barkcloud.identity.IdentityApi/Auth", _ => new AuthResponse
                {
                    AccessToken = new Token
                    {
                        Value = "access-token",
                        ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1)),
                    },
                    RefreshToken = new Token { Value = "refresh-token" },
                });
            var userInvoker = new FakeCallInvoker();
            userInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser",
                _ => new GetUserResponse { User = new User { Id = 42, Username = "user" } });
            var profile = new UserProfile(new UsersApi.UsersApiClient(userInvoker), _ => { });

            Gateway = Cloud.CreateGateway(Temp.Path("cache"), Time);
            var outbox = new UploadOutbox(Temp.Path("outbox"));
            var fileSystem = new BarkCloudFileSystem(Gateway, Temp.Path("write"), outbox, "server",
                () => profile.UserId);
            Worker = new UploadOutboxWorker(outbox,
                () => ("server", profile.UserId),
                (entry, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(entry, cancellationToken),
                fileSystem.CompleteOutboxEntry,
                time: Time, deferDataDeletion: fileSystem.DeferOutboxDataDeletion);
            Lifetime = new CancellationTokenSource();
            Engine = new DriveEngine(new TokenManager(new IdentityApi.IdentityApiClient(identityInvoker),
                    new TokenStore(Temp.Path("tokens"))), Gateway, new MountManager(), fileSystem, Lifetime,
                settings, profile, Worker, "server", "files.test");
        }

        private CloudGateway Gateway { get; }
        private UploadOutboxWorker Worker { get; }
        private CancellationTokenSource Lifetime { get; }

        public async Task<EngineStatus> LoginAsync(string login = "user")
        {
            var status = await Engine.LoginAsync(login, "password", null);
            status.Error.Should().BeNull();
            status.Authenticated.Should().BeTrue();
            return status;
        }

        public async ValueTask DisposeAsync()
        {
            Lifetime.Dispose();
            await Worker.DisposeAsync();
            Gateway.Dispose();
            Temp.Dispose();
        }
    }
}
