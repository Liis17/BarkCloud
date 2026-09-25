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
            HasSecretKey = true,
            QuotaBytes = 12_345
        });

        dto.HasSecretKey.Should().BeTrue();
        dto.GetType().GetProperty("AccessKey").Should().BeNull();
        dto.GetType().GetProperty("SecretKey").Should().BeNull();
        dto.QuotaBytes.Should().Be("12345");
    }

    [Fact]
    public async Task SaveStorageProfile_PassesIntegerQuotaValueAndUnitToConfigurationService()
    {
        var configuration = new Mock<ConfigurationApi.ConfigurationApiClient>();
        configuration
            .Setup(client => client.GetStorageProfilesAsync(It.IsAny<GetStorageProfilesRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetStorageProfilesResponse()));
        configuration
            .Setup(client => client.SaveStorageProfileAsync(
                It.Is<SaveStorageProfileRequest>(request => request.QuotaValue == "2" && request.QuotaUnit == "tb"),
                null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new SaveStorageProfileResponse { Success = true }));
        var gateway = new ConfigurationManagementGateway(
            configuration.Object,
            new ConfigurationBuilder().Build(),
            new S3AccessChecker());

        var result = await gateway.SaveStorageProfileAsync(new StorageProfileEdit(
            "images", "https://s3.example", "access", "secret", "images", false, false, null, false, "2", "tb"),
            "user:1");

        result.Success.Should().BeTrue();
        result.RestartTargets.Should().Contain("files");
        configuration.Verify(client => client.SaveStorageProfileAsync(
            It.Is<SaveStorageProfileRequest>(request => request.QuotaValue == "2" && request.QuotaUnit == "tb"),
            null, null, default), Times.Once);
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
            new ConfigurationBuilder().Build(),
            new S3AccessChecker()).GetAsync();

        dto.Settings.Should().ContainSingle(item => item.Section == "JwtSettings" && item.Key == "Issuer");
        dto.Settings.Should().NotContain(item => item.Key == "Token");
        dto.StorageProfiles.Single().GetType().GetProperty("AccessKey").Should().BeNull();
    }

    [Fact]
    public async Task CheckStorageProfileAccess_UsesSavedCredentialsWhenEditLeavesThemBlank()
    {
        var configuration = new Mock<ConfigurationApi.ConfigurationApiClient>();
        configuration
            .Setup(client => client.GetStorageProfilesAsync(It.IsAny<GetStorageProfilesRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetStorageProfilesResponse
            {
                Profiles =
                {
                    new StorageProfileItem
                    {
                        ProfileId = "images-v1",
                        Role = "images",
                        ServiceUrl = "http://minio:9000",
                        AccessKey = "saved-access",
                        SecretKey = "saved-secret",
                        BucketName = "saved-bucket",
                        IsActive = true,
                        Version = 1
                    }
                }
            }));
        var checker = new CapturingS3AccessChecker();
        var gateway = new ConfigurationManagementGateway(
            configuration.Object,
            new ConfigurationBuilder().Build(),
            checker);

        var result = await gateway.CheckStorageProfileAccessAsync(new StorageProfileEdit(
            "images",
            "http://new-minio:9000",
            "",
            "",
            "new-bucket",
            false,
            false,
            "images-v1",
            false));

        result.Success.Should().BeTrue();
        checker.Request.Should().NotBeNull();
        checker.Request!.AccessKey.Should().Be("saved-access");
        checker.Request.SecretKey.Should().Be("saved-secret");
        checker.Request.ServiceUrl.Should().Be("http://new-minio:9000");
        checker.Request.BucketName.Should().Be("new-bucket");
    }

    private sealed class CapturingS3AccessChecker : S3AccessChecker
    {
        public S3AccessCheckRequest? Request { get; private set; }

        public override Task<StorageAccessCheckResult> CheckAsync(
            S3AccessCheckRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new StorageAccessCheckResult(true, "ok"));
        }
    }
}
