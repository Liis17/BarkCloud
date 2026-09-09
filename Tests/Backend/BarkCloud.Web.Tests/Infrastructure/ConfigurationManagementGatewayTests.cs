using BarkCloud.Proto.Configuration;
using BarkCloud.Web.Infrastructure;

namespace BarkCloud.Web.Tests.Infrastructure;

public class ConfigurationManagementGatewayTests
{
    [Fact]
    public void Mask_SensitiveConfigurationNeverContainsRawValue()
    {
        var dto = ConfigurationManagementGateway.Mask(new ConfigurationItem
        {
            Section = "JwtSettings",
            Key = "SecretKey",
            Value = "raw-secret",
            IsSensitive = true,
            HasValue = true
        });

        dto.Value.Should().BeEmpty();
        dto.HasValue.Should().BeTrue();
    }

    [Fact]
    public void Mask_StorageProfileNeverContainsSecretAndMasksAccessKey()
    {
        var dto = ConfigurationManagementGateway.Mask(new StorageProfileItem
        {
            ProfileId = "universal-v1",
            AccessKey = "visible-access",
            SecretKey = "raw-secret",
            HasSecretKey = true
        });

        dto.AccessKey.Should().NotContain("visible-access");
        dto.HasSecretKey.Should().BeTrue();
        dto.GetType().GetProperty("SecretKey").Should().BeNull();
    }
}
