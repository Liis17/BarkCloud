using BarkCloud.Configuration.Infrastructure;
using BarkCloud.Proto.Configuration;
using BarkCloud.Shared.Identity;
using BarkCloud.TestKit;

using Grpc.Core;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Configuration.Tests.Infrastructure;

public class ConfigurationAccessInterceptorTests
{
    private const string Registry = "Identity=id-key;Users=users-key;Notification=notification-key;Files=files-key;Web=web-key;Torrent=torrent-key";
    private const string Base = "/barkcloud.configuration.ConfigurationApi/";
    private static ConfigurationAccessInterceptor Build(string? registry = Registry)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CONFIGURATION_ACCESS_KEYS"] = registry
            })
            .Build();
        return new ConfigurationAccessInterceptor(config, NullLogger<ConfigurationAccessInterceptor>.Instance);
    }

    private static ServerCallContext Context(string method, string? key = null)
    {
        var context = new TestServerCallContext(method);
        if (key is not null)
            context.RequestHeaders.Add(ConfigurationAccessInterceptor.HeaderName, key);
        return context;
    }

    private static Task Call(
        ConfigurationAccessInterceptor interceptor,
        string method,
        string? key,
        GetConfigurationRequest? configurationRequest = null)
    {
        object request;
        object response;
        if (configurationRequest is not null)
        {
            request = configurationRequest;
            response = new GetConfigurationResponse();
        }
        else
        {
            request = new GetReadinessRequest();
            response = new GetReadinessResponse();
        }

        return InterceptorHelper.Invoke(interceptor, request, Context(method, key), response);
    }

    private static class InterceptorHelper
    {
        public static async Task Invoke<TRequest, TResponse>(
            ConfigurationAccessInterceptor interceptor,
            TRequest request,
            ServerCallContext context,
            TResponse response)
            where TRequest : class
            where TResponse : class
        {
            await interceptor.UnaryServerHandler(
                request,
                context,
                (_, _) => Task.FromResult(response));
        }
    }

    [Fact]
    public async Task GetReadiness_AcceptsAnyRegisteredService()
    {
        var sut = Build();

        var act = () => Call(sut, Base + "GetReadiness", key: "torrent-key");

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-registered")]
    [InlineData("web-key ")]
    public async Task AnyMethod_UnknownKey_IsUnauthenticated(string? key)
    {
        var sut = Build();

        var act = () => Call(sut, Base + "GetReadiness", key: key);

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task EmptyRegistry_FailsClosed()
    {
        var sut = Build(registry: "");

        var act = () => Call(sut, Base + "GetReadiness", key: "web-key");

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task GetConfiguration_OwnScope_IsAllowed()
    {
        var sut = Build();

        var act = () => Call(sut, Base + "GetConfiguration", key: "torrent-key",
            configurationRequest: new GetConfigurationRequest { ServiceId = (int)ServiceId.Torrent });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetConfiguration_GlobalScope_IsAllowedForAnyService()
    {
        var sut = Build();

        var act = () => Call(sut, Base + "GetConfiguration", key: "id-key",
            configurationRequest: new GetConfigurationRequest { ServiceId = 0 });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetConfiguration_ForeignScope_IsDenied()
    {
        var sut = Build();

        var act = () => Call(sut, Base + "GetConfiguration", key: "torrent-key",
            configurationRequest: new GetConfigurationRequest { ServiceId = (int)ServiceId.Files });

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Theory]
    [InlineData("files-key", true)]   // Files читает S3-профили при старте
    [InlineData("web-key", true)]     // Web — раздел настроек и миграция
    [InlineData("id-key", false)]
    [InlineData("torrent-key", false)]
    public async Task GetStorageProfiles_OnlyWebAndFiles(string key, bool allowed)
    {
        var sut = Build();

        var act = () => Call(sut, Base + "GetStorageProfiles", key: key);

        if (allowed)
            await act.Should().NotThrowAsync();
        else
            await act.Should().ThrowAsync<RpcException>()
                .Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Theory]
    [InlineData("UpdateConfiguration")]
    [InlineData("GetAllConfigurations")]
    [InlineData("GetConfigurationHistory")]
    [InlineData("RollbackConfiguration")]
    [InlineData("AddReservedName")]
    [InlineData("UpdateReservedName")]
    [InlineData("DeleteReservedName")]
    [InlineData("GetReservedNames")]
    [InlineData("SaveStorageProfile")]
    [InlineData("ActivateStorageProfile")]
    [InlineData("DisableStorageRole")]
    [InlineData("RelocateStorageProfiles")]
    [InlineData("SaveStorageQuotaGroup")]
    [InlineData("DeleteStorageQuotaGroup")]
    public async Task AdministrativeMethods_OnlyWeb(string method)
    {
        var sut = Build();

        var webCall = () => Call(sut, Base + method, key: "web-key");
        var torrentCall = () => Call(sut, Base + method, key: "torrent-key");

        await webCall.Should().NotThrowAsync();
        await torrentCall.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task UnlistedConfigurationMethod_DefaultDenyForNonWeb()
    {
        var sut = Build();

        // Нераспределённые методы ConfigurationApi по умолчанию доступны только Web.
        var webCall = () => Call(sut, Base + "NotExistingRpc", key: "web-key");
        var torrentCall = () => Call(sut, Base + "NotExistingRpc", key: "torrent-key");

        await webCall.Should().NotThrowAsync();
        await torrentCall.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task ForeignServiceMethod_IsDenied()
    {
        var sut = Build();

        var act = () => Call(sut, "/barkcloud.users.UsersApi/GetUser", key: "web-key");

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }
}
