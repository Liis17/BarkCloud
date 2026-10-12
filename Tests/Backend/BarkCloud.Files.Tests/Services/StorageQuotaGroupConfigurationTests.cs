using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Configurations;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer;
using BarkCloud.Proto.Configuration;
using BarkCloud.Shared.Identity;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using System.Net;
using System.Net.Sockets;

namespace BarkCloud.Files.Tests.Services;

public sealed class StorageQuotaGroupConfigurationTests
{
    [Fact]
    public async Task LoadConfiguration_BindsMultipleQuotaGroupsAndMembersIncludingR2()
    {
        var oldServiceUrl = Environment.GetEnvironmentVariable("CONFIGURATION_SERVICE_URL");
        var oldAccessKey = Environment.GetEnvironmentVariable("CONFIGURATION_ACCESS_KEY");
        var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();

        var profiles = new GetStorageProfilesResponse();
        profiles.Profiles.Add(new StorageProfileItem
        {
            ProfileId = "images-v1",
            Role = "images",
            ServiceUrl = "http://account.r2.example:9000",
            AccessKey = "access",
            SecretKey = "secret",
            BucketName = "r2-main",
            IsR2 = true,
            IsActive = true
        });
        var main = new StorageQuotaGroupItem { Id = "grp-main", Name = "Main", QuotaBytes = 100 };
        main.Buckets.Add(new StorageBucketRef { ServiceUrl = "https://s3.example", BucketName = "photos" });
        main.Buckets.Add(new StorageBucketRef { ServiceUrl = "https://s3.example", BucketName = "videos" });
        var r2 = new StorageQuotaGroupItem { Id = "grp-r2", Name = "R2", QuotaBytes = 0 };
        r2.Buckets.Add(new StorageBucketRef { ServiceUrl = "https://account.r2.example", BucketName = "r2-main" });
        r2.Buckets.Add(new StorageBucketRef { ServiceUrl = "https://account.r2.example", BucketName = "r2-archive" });
        profiles.QuotaGroups.Add(main);
        profiles.QuotaGroups.Add(r2);

        var serverBuilder = WebApplication.CreateBuilder();
        serverBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port,
            listenOptions => listenOptions.Protocols = HttpProtocols.Http2));
        serverBuilder.Services.AddGrpc();
        serverBuilder.Services.AddSingleton(new FakeConfigurationApi(profiles));
        await using var server = serverBuilder.Build();
        server.MapGrpcService<FakeConfigurationApi>();
        await server.StartAsync();

        try
        {
            Environment.SetEnvironmentVariable("CONFIGURATION_SERVICE_URL", $"http://127.0.0.1:{port}");
            Environment.SetEnvironmentVariable("CONFIGURATION_ACCESS_KEY", null);

            var clientBuilder = WebApplication.CreateBuilder();
            clientBuilder.LoadConfiguration(ServiceId.Files);
            clientBuilder.Configuration["StorageQuotaGroups:grp-main:Name"].Should().Be("Main");
            clientBuilder.Configuration["StorageQuotaGroups:grp-main:Buckets:1:BucketName"].Should().Be("videos");
            clientBuilder.Configuration["StorageQuotaGroups:grp-r2:Buckets:1:ServiceUrl"]
                .Should().Be("https://account.r2.example");

            var s3Client = new Mock<IAmazonS3>();
            s3Client.Setup(client => client.ListObjectsV2Async(
                    It.Is<ListObjectsV2Request>(request => request.BucketName == "photos"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Page("photos", 10));
            s3Client.Setup(client => client.ListObjectsV2Async(
                    It.Is<ListObjectsV2Request>(request => request.BucketName == "videos"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Page("videos", 20));
            s3Client.Setup(client => client.ListObjectsV2Async(
                    It.Is<ListObjectsV2Request>(request => request.BucketName == "r2-main"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Page("r2-main", 30));
            s3Client.Setup(client => client.ListObjectsV2Async(
                    It.Is<ListObjectsV2Request>(request => request.BucketName == "r2-archive"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Page("r2-archive", 40));
            var registry = new Mock<S3BucketRegistry>(new ConfigurationBuilder().Build());
            registry.Setup(value => value.GetAllProfiles()).Returns(
            [
                (Profile("photos", "https://s3.example", "photos"), s3Client.Object),
                (Profile("videos", "https://s3.example", "videos"), s3Client.Object),
                (Profile("r2-main", "http://account.r2.example:9000", "r2-main", isR2: true), s3Client.Object),
                (Profile("r2-archive", "http://account.r2.example:9000", "r2-archive", isR2: true), s3Client.Object)
            ]);
            var stats = await new S3StorageStatsProvider(registry.Object, clientBuilder.Configuration,
                TimeProvider.System, NullLogger<S3StorageStatsProvider>.Instance).GetStatsAsync();

            stats.Groups.Should().ContainSingle(group => group.Id == "grp-main" && group.Name == "Main"
                && group.UsedBytes == 30 && group.QuotaBytes == 100 && group.UnmeasuredBuckets == 0);
            stats.Groups.Should().ContainSingle(group => group.Id == "grp-r2" && group.Name == "R2"
                && group.UsedBytes == 70 && group.QuotaBytes == 0 && group.UnmeasuredBuckets == 0);
            foreach (var bucketName in new[] { "photos", "videos", "r2-main", "r2-archive" })
                s3Client.Verify(client => client.ListObjectsV2Async(
                    It.Is<ListObjectsV2Request>(request => request.BucketName == bucketName),
                    It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIGURATION_SERVICE_URL", oldServiceUrl);
            Environment.SetEnvironmentVariable("CONFIGURATION_ACCESS_KEY", oldAccessKey);
            await server.StopAsync();
        }
    }

    private sealed class FakeConfigurationApi(GetStorageProfilesResponse profiles)
        : ConfigurationApi.ConfigurationApiBase
    {
        public override Task<GetConfigurationResponse> GetConfiguration(
            GetConfigurationRequest request,
            Grpc.Core.ServerCallContext context) => Task.FromResult(new GetConfigurationResponse());

        public override Task<GetStorageProfilesResponse> GetStorageProfiles(
            GetStorageProfilesRequest request,
            Grpc.Core.ServerCallContext context) => Task.FromResult(profiles);
    }

    private static StorageProfileOptions Profile(string id, string serviceUrl, string bucketName, bool isR2 = false) => new()
    {
        ProfileId = id,
        Role = id,
        ServiceUrl = serviceUrl,
        AccessKey = "access",
        SecretKey = "secret",
        BucketName = bucketName,
        IsR2 = isR2,
        IsActive = true
    };

    private static ListObjectsV2Response Page(string key, long size) => new()
    {
        S3Objects = [new S3Object { Key = key, Size = size }]
    };
}
