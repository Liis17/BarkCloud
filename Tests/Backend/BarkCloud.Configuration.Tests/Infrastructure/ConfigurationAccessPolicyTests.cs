using BarkCloud.Configuration.Infrastructure;

namespace BarkCloud.Configuration.Tests.Infrastructure;

public class ConfigurationAccessPolicyTests
{
    [Fact]
    public void EnsureConfigured_ProductionWithoutRegistry_ThrowsClearError()
    {
        var act = () => ConfigurationAccessPolicy.EnsureConfigured(isDevelopment: false, accessKeys: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CONFIGURATION_ACCESS_KEYS*");
    }

    [Fact]
    public void EnsureConfigured_DevelopmentWithoutRegistry_IsAllowedButFailClosed()
    {
        var act = () => ConfigurationAccessPolicy.EnsureConfigured(isDevelopment: true, accessKeys: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureConfigured_DuplicateServiceInRegistry_Throws()
    {
        var act = () => ConfigurationAccessPolicy.EnsureConfigured(
            isDevelopment: false,
            accessKeys: "Users=key1;users=key2");

        act.Should().Throw<InvalidOperationException>().WithMessage("*дубликаты*");
    }

    [Fact]
    public void EnsureConfigured_ValidRegistry_DoesNotThrow()
    {
        var act = () => ConfigurationAccessPolicy.EnsureConfigured(
            isDevelopment: false,
            accessKeys: "Identity=k1;Users=k2;Notification=k3;Files=k4;Web=k5;Torrent=k6");

        act.Should().NotThrow();
    }
}
