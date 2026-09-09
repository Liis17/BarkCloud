using BarkCloud.Configuration.Infrastructure;

namespace BarkCloud.Configuration.Tests.Infrastructure;

public class ConfigurationAccessPolicyTests
{
    [Fact]
    public void EnsureConfigured_ProductionWithoutKey_ThrowsClearError()
    {
        var act = () => ConfigurationAccessPolicy.EnsureConfigured(isDevelopment: false, accessKey: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CONFIGURATION_ACCESS_KEY*");
    }

    [Fact]
    public void EnsureConfigured_DevelopmentWithoutKey_IsAllowed()
    {
        var act = () => ConfigurationAccessPolicy.EnsureConfigured(isDevelopment: true, accessKey: null);

        act.Should().NotThrow();
    }
}
