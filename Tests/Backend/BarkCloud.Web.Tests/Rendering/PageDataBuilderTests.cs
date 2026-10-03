using System.Diagnostics;
using System.Text.Json;

using BarkCloud.Proto.Configuration;
using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.TestKit;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using Grpc.Core;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Web.Tests.Rendering;

public class PageDataBuilderTests
{
    private readonly Mock<UsersApi.UsersApiClient> _users = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersServer = new();
    private readonly Mock<FilesApi.FilesApiClient> _files = new();
    private static readonly WebUser User = new() { UserId = 42, AccessToken = "user-token", IssuedAt = DateTime.UtcNow };

    [Fact]
    public async Task LeanShellAndSettingsContextAndProfile_CompleteWhileStorageIsBlocked()
    {
        using var scan = new CancellationTokenSource();
        _files.Setup(client => client.GetUserStorageInfoAsync(It.IsAny<GetUserStorageInfoRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Unary(DelayedStorage(scan.Token)));
        SetProfile();
        var sut = Create();
        var legacy = sut.BuildShellAsync(User, new DefaultHttpContext());
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var shell = await sut.BuildShellAsync(User, new DefaultHttpContext(), includeStorage: false)
                .WaitAsync(TimeSpan.FromSeconds(1));
            var context = sut.BuildSettingsContext(new DefaultHttpContext());
            var profile = await sut.BuildProfileAsync(User, default).WaitAsync(TimeSpan.FromSeconds(1));

            shell["user.display_name"].Should().Be("Test User");
            JsonSerializer.Serialize(context).Should().Contain("\"unlocked\":false");
            JsonSerializer.Serialize(profile).Should().Contain("test@example.com");
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
            legacy.IsCompleted.Should().BeFalse();
            _files.Verify(client => client.GetUserStorageInfoAsync(It.IsAny<GetUserStorageInfoRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            scan.Cancel();
            try { await legacy; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Storage_RequestsNonBlockingSnapshotAndKeepsSeparateStatesAndTimestamps()
    {
        var updatedAt = DateTime.UtcNow.AddMinutes(-6);
        _files.Setup(client => client.GetUserStorageInfoAsync(It.Is<GetUserStorageInfoRequest>(request => request.NonBlockingStats),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserStorageInfoResponse
            {
                TotalAvailableStorage = 100, DiskUsedStorage = 25,
                PhysicalStatsState = "refreshing",
                PhysicalStatsUpdatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(updatedAt),
                AllS3StatsState = "loading"
            }));

        var json = JsonSerializer.SerializeToElement(await Create().BuildStorageAsync(User));

        json.GetProperty("state").GetString().Should().Be("refreshing");
        json.GetProperty("updatedAt").GetDateTimeOffset().UtcDateTime.Should().Be(updatedAt);
        json.GetProperty("allS3State").GetString().Should().Be("loading");
        json.GetProperty("allS3UpdatedAt").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("percent").GetInt32().Should().Be(25);
    }

    [Fact]
    public async Task LeanShell_WhenProfileFails_DoesNotReturnFictitiousUser()
    {
        _users.Setup(client => client.GetUserAsync(It.IsAny<GetUserRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(Unary(Task.FromException<GetUserResponse>(new RpcException(new Status(StatusCode.Unavailable, "offline")))));

        var action = () => Create().BuildShellAsync(User, new DefaultHttpContext(), false);

        await action.Should().ThrowAsync<RpcException>();
    }

    private void SetProfile()
    {
        _users.Setup(client => client.GetUserAsync(It.IsAny<GetUserRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserResponse
            {
                User = new User { FirstName = "Test", LastName = "User", Username = "test" }
            }));
        _usersServer.Setup(client => client.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse { Contact = new UserContact { Email = "test@example.com" } }));
    }

    private PageDataBuilder Create()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:AdminPassword"] = "admin", ["JwtSettings:SecretKey"] = "test-secret-key-at-least-32-bytes-long"
        }).Build();
        return new(_users.Object, _usersServer.Object, _files.Object, new Mock<IdentityApi.IdentityApiClient>().Object,
            new AdminGate(config), config, new FeatureConfigurationGateway(new Mock<ConfigurationApi.ConfigurationApiClient>().Object,
                config, NullLogger<FeatureConfigurationGateway>.Instance), NullLogger<PageDataBuilder>.Instance);
    }

    private static async Task<GetUserStorageInfoResponse> DelayedStorage(CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), token);
        return new();
    }

    private static AsyncUnaryCall<T> Unary<T>(Task<T> task) where T : class =>
        new(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
}
