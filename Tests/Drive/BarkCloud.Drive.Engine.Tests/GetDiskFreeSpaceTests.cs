using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using DokanNet;

using FluentAssertions;

using Grpc.Core;

namespace BarkCloud.Drive.Engine.Tests;

// Ёмкость диска для Проводника: личный лимит → сумма конечных квот S3-групп → физический диск хоста.
public sealed class GetDiskFreeSpaceTests
{
    private const long GB = 1024L * 1024 * 1024;
    private const long TB = 1024L * GB;

    [Fact]
    public void S3_quota_groups_replace_host_disk_capacity_when_no_personal_limit()
    {
        using var harness = CreateHarness();
        // Хост 115 ГБ, но пользователь без личного лимита: ёмкость — S3-группа 1 ТБ.
        harness.SetupStorage(new GetUserStorageInfoResponse
        {
            StorageLimit = 0,
            TotalAvailableStorage = 115 * GB,
            DiskUsedStorage = 10 * GB,
            S3UsedStorage = 5 * GB,
            AllS3StatsAvailable = true,
            S3QuotaGroups =
            {
                new S3QuotaGroupUsage { Id = "g1", Name = "External", UsedBytes = 100 * GB, QuotaBytes = TB },
                new S3QuotaGroupUsage { Id = "ung", Name = "", UsedBytes = 50 * GB, IsUngrouped = true },
                new S3QuotaGroupUsage { Id = "g2", Name = "Unlimited", UsedBytes = 20 * GB },
            },
        });

        harness.FileSystem.GetDiskFreeSpace(out var free, out var total, out var totalFree, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        total.Should().Be(TB);
        free.Should().Be(TB - 100 * GB);
        totalFree.Should().Be(TB - 100 * GB);
    }

    [Fact]
    public void Multiple_finite_groups_sum_quotas_and_clamp_exceeded_remainders()
    {
        using var harness = CreateHarness();
        harness.SetupStorage(new GetUserStorageInfoResponse
        {
            StorageLimit = 0,
            AllS3StatsAvailable = true,
            S3QuotaGroups =
            {
                new S3QuotaGroupUsage { Id = "a", Name = "A", UsedBytes = 900, QuotaBytes = 1000 },
                new S3QuotaGroupUsage { Id = "b", Name = "B", UsedBytes = 400, QuotaBytes = 300 }, // превышена
            },
        });

        harness.FileSystem.GetDiskFreeSpace(out var free, out var total, out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        total.Should().Be(1300);
        free.Should().Be(100); // остаток превышенной группы ограничен нулём
    }

    [Fact]
    public void Personal_limit_with_reservations_takes_priority_over_s3_groups()
    {
        using var harness = CreateHarness();
        harness.SetupStorage(new GetUserStorageInfoResponse
        {
            StorageLimit = 500,
            TotalUsedStorage = 400,
            ReservedStorage = 50,
            AllS3StatsAvailable = true,
            S3QuotaGroups =
            {
                new S3QuotaGroupUsage { Id = "a", Name = "A", UsedBytes = 0, QuotaBytes = TB },
            },
        });

        harness.FileSystem.GetDiskFreeSpace(out var free, out var total, out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        total.Should().Be(500);
        free.Should().Be(50);
    }

    [Theory]
    [InlineData(false)]   // старый сервер: групп нет, статистика S3 недоступна
    [InlineData(true)]    // статистика недоступна, хотя группы перечислены
    public void Unavailable_s3_stats_falls_back_to_physical_disk(bool withGroups)
    {
        using var harness = CreateHarness();
        var response = new GetUserStorageInfoResponse
        {
            StorageLimit = 0,
            TotalAvailableStorage = 1000,
            DiskUsedStorage = 100,
            S3UsedStorage = 200,
            AllS3StatsAvailable = false,
        };
        if (withGroups)
            response.S3QuotaGroups.Add(new S3QuotaGroupUsage { Id = "a", Name = "A", UsedBytes = 1, QuotaBytes = 100 });
        harness.SetupStorage(response);

        harness.FileSystem.GetDiskFreeSpace(out var free, out var total, out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        total.Should().Be(1000);
        free.Should().Be(700);
    }

    [Fact]
    public void Only_unlimited_and_ungrouped_groups_fall_back_to_physical_disk()
    {
        using var harness = CreateHarness();
        harness.SetupStorage(new GetUserStorageInfoResponse
        {
            StorageLimit = 0,
            TotalAvailableStorage = 1000,
            DiskUsedStorage = 100,
            S3UsedStorage = 200,
            AllS3StatsAvailable = true,
            S3QuotaGroups =
            {
                new S3QuotaGroupUsage { Id = "u", Name = "Unlimited", UsedBytes = 10 },
                new S3QuotaGroupUsage { Id = "ung", Name = "", UsedBytes = 30, QuotaBytes = 500, IsUngrouped = true },
            },
        });

        harness.FileSystem.GetDiskFreeSpace(out var free, out var total, out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        total.Should().Be(1000);
        free.Should().Be(700);
    }

    [Fact]
    public void Unmeasured_buckets_still_count_towards_capacity()
    {
        using var harness = CreateHarness();
        // Свободное место такой группы — оценка, но ёмкость считается по квоте.
        harness.SetupStorage(new GetUserStorageInfoResponse
        {
            StorageLimit = 0,
            AllS3StatsAvailable = true,
            S3QuotaGroups =
            {
                new S3QuotaGroupUsage
                {
                    Id = "a", Name = "A", UsedBytes = 200, QuotaBytes = 1000, UnmeasuredBuckets = 2,
                },
            },
        });

        harness.FileSystem.GetDiskFreeSpace(out var free, out var total, out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        total.Should().Be(1000);
        free.Should().Be(800);
    }

    [Fact]
    public void Failure_without_snapshot_returns_dokan_error_instead_of_empty_disk()
    {
        using var harness = CreateHarness();
        harness.Cloud.Invoker.FailNext(FakeCloudMethods.GetUserStorageInfo,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));

        harness.FileSystem.GetDiskFreeSpace(out var free, out var total, out _, new FakeDokanFileInfo())
            .Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);

        free.Should().Be(0);
        total.Should().Be(0);
    }

    private sealed class Harness : IDisposable
    {
        public TestDirectory Temp { get; } = new();
        public FakeCloud Cloud { get; } = new();
        public CloudGateway Gateway { get; }

        public Harness()
        {
            Gateway = Cloud.CreateGateway(Temp.Path("cache"));
            FileSystem = new BarkCloudFileSystem(Gateway, Temp.Path("write"));
        }

        public BarkCloudFileSystem FileSystem { get; }

        public void SetupStorage(GetUserStorageInfoResponse response)
            => Cloud.Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
                FakeCloudMethods.GetUserStorageInfo, _ => response);

        public void Dispose()
        {
            Gateway.Dispose();
            Temp.Dispose();
        }
    }

    private static Harness CreateHarness() => new();
}
