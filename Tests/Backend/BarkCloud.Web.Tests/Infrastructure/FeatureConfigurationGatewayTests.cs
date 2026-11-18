using BarkCloud.Proto.Configuration;
using BarkCloud.TestKit;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Web.Tests.Infrastructure;

public class FeatureConfigurationGatewayTests
{
    private readonly Mock<ConfigurationApi.ConfigurationApiClient> _configuration = new();
    private readonly GetConfigurationResponse _response = new()
    {
        Configurations =
        {
            new ConfigurationItem { Section = "Features", Key = "RegistrationEnabled", Value = "false" },
            new ConfigurationItem { Section = "Features", Key = "TorrentEnabled", Value = "false" }
        }
    };

    public FeatureConfigurationGatewayTests()
    {
        _configuration.Setup(client => client.GetConfigurationAsync(It.IsAny<GetConfigurationRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => GrpcCallHelpers.AsyncUnary(_response));
    }

    [Fact]
    public async Task TorrentFlag_IsReadOnceWithinTtl_RegistrationIsReadEveryTime()
    {
        var gateway = Create();

        (await gateway.TorrentEnabledAsync()).Should().BeFalse();
        (await gateway.TorrentEnabledAsync()).Should().BeFalse();
        Calls().Should().Be(1);

        (await gateway.RegistrationEnabledAsync()).Should().BeFalse();
        (await gateway.RegistrationEnabledAsync()).Should().BeFalse();
        Calls().Should().Be(3);
    }

    private int Calls() => _configuration.Invocations.Count(i => i.Method.Name == nameof(ConfigurationApi.ConfigurationApiClient.GetConfigurationAsync));

    [Fact]
    public async Task SetTorrentEnabled_IsVisibleImmediatelyDespiteCachedSnapshot()
    {
        _configuration.Setup(client => client.UpdateConfigurationAsync(It.IsAny<UpdateConfigurationRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new UpdateConfigurationResponse { Success = true }));
        var gateway = Create();
        (await gateway.TorrentEnabledAsync()).Should().BeFalse();

        await gateway.SetTorrentEnabledAsync(true, "user:1", "test");

        (await gateway.TorrentEnabledAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task UnavailableConfiguration_FallsBackToEnabledByDefault()
    {
        _configuration.Setup(client => client.GetConfigurationAsync(It.IsAny<GetConfigurationRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "offline")));

        (await Create().TorrentEnabledAsync()).Should().BeTrue();
    }

    private FeatureConfigurationGateway Create() => new(_configuration.Object, new ConfigurationBuilder().Build(),
        NullLogger<FeatureConfigurationGateway>.Instance);
}
