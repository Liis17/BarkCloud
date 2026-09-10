using BarkCloud.Proto.Configuration;
using BarkCloud.TestKit;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

using Microsoft.Extensions.Configuration;

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
    public void Mask_StorageProfileNeverContainsCredentials()
    {
        var dto = ConfigurationManagementGateway.Mask(new StorageProfileItem
        {
            ProfileId = "universal-v1",
            AccessKey = "visible-access",
            SecretKey = "raw-secret",
            HasSecretKey = true
        });

        dto.HasSecretKey.Should().BeTrue();
        dto.GetType().GetProperty("AccessKey").Should().BeNull();
        dto.GetType().GetProperty("SecretKey").Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_DoesNotExposeTokenSettingsOrStorageCredentials()
    {
        var configuration = new Mock<ConfigurationApi.ConfigurationApiClient>();
        configuration
            .Setup(client => client.GetAllConfigurationsAsync(It.IsAny<GetAllConfigurationsRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetAllConfigurationsResponse
            {
                Configurations =
                {
                    new ConfigurationItem { Section = "UsersService", Key = "Token", Value = "raw-token", IsSensitive = true, HasValue = true },
                    new ConfigurationItem { Section = "JwtSettings", Key = "Issuer", Value = "bark" }
                }
            }));
        configuration
            .Setup(client => client.GetReservedNamesAsync(It.IsAny<GetReservedNamesRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetReservedNamesResponse()));
        configuration
            .Setup(client => client.GetStorageProfilesAsync(It.IsAny<GetStorageProfilesRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetStorageProfilesResponse
            {
                Profiles =
                {
                    new StorageProfileItem { AccessKey = "raw-access", SecretKey = "raw-secret", HasSecretKey = true }
                }
            }));

        var dto = await new ConfigurationManagementGateway(
            configuration.Object,
            new ConfigurationBuilder().Build()).GetAsync();

        dto.Settings.Should().ContainSingle(item => item.Section == "JwtSettings" && item.Key == "Issuer");
        dto.Settings.Should().NotContain(item => item.Key == "Token");
        dto.StorageProfiles.Single().GetType().GetProperty("AccessKey").Should().BeNull();
    }
}
