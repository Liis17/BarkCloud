using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;

using Microsoft.Extensions.Configuration;

namespace BarkCloud.Files.Tests.Infrastructure;

public sealed class S3ProfileRegistryTests
{
    [Fact]
    public void ResolveWriteProfile_UsesSpecializedRoleAndUniversalFallback()
    {
        using var registry = new S3BucketRegistry(Configuration(
            Profile("universal-v1", "universal", "https://local.example", "universal", true),
            Profile("images-v1", "images", "https://r2.example", "images", true),
            Profile("previews-v1", "previews", "https://local.example", "previews", true)));

        registry.ResolveWriteProfileId(UploadFileType.CloudFile, MediaKind.Photo, isPreview: false)
            .Should().Be("images-v1");
        registry.ResolveWriteProfileId(UploadFileType.CloudFile, MediaKind.Video, isPreview: false)
            .Should().Be("universal-v1");
        registry.ResolveWriteProfileId(UploadFileType.CloudFile, MediaKind.Photo, isPreview: true)
            .Should().Be("previews-v1");
    }

    [Fact]
    public void Profiles_WithSameBucketNameOnDifferentEndpoints_KeepDifferentClients()
    {
        using var registry = new S3BucketRegistry(Configuration(
            Profile("universal-v1", "universal", "https://one.example", "same", true),
            Profile("images-v1", "images", "https://two.example", "same", true)));

        registry.GetClientForProfile("universal-v1")
            .Should().NotBeSameAs(registry.GetClientForProfile("images-v1"));
    }

    [Fact]
    public void ResolveReadProfile_EmptyMigratingRow_UsesLegacyProfileIdNotPhysicalBucketName()
    {
        using var registry = new S3BucketRegistry(Configuration(
            Profile("universal-v1", "universal", "https://local.example", "cloud-universal", true),
            Profile("cloud-files-old-v1", "cloud-files-old", "https://legacy.example", "legacy-bucket", false, isLegacy: true)));

        var profileId = registry.ResolveReadProfileId(new UploadFile
        {
            Type = UploadFileType.CloudFile,
            MediaKind = MediaKind.Photo,
            StorageProfileId = string.Empty
        });

        profileId.Should().Be("cloud-files-old-v1");
    }

    [Fact]
    public void ResolveWriteProfile_ConfiguredSpecializedProfileIsNotReplacedByUniversal()
    {
        using var registry = new S3BucketRegistry(Configuration(
            Profile("universal-v1", "universal", "https://local.example", "universal", true),
            Profile("videos-v2", "videos", "https://unavailable.example", "videos", true)));

        registry.ResolveWriteProfileId(UploadFileType.CloudFile, MediaKind.Video, isPreview: false)
            .Should().Be("videos-v2");
    }

    [Theory]
    [InlineData(UploadFileType.UserAvatar, MediaKind.Photo, false, "avatars-v1")]
    [InlineData(UploadFileType.UserAvatar, MediaKind.Photo, true, "previews-v1")]
    [InlineData(UploadFileType.CloudFile, MediaKind.Photo, false, "images-v1")]
    [InlineData(UploadFileType.CloudFile, MediaKind.Video, false, "videos-v1")]
    [InlineData(UploadFileType.CloudFile, MediaKind.Audio, false, "audio-v1")]
    [InlineData(UploadFileType.CloudFile, MediaKind.Document, false, "documents-v1")]
    [InlineData(UploadFileType.CloudFile, MediaKind.Other, false, "other-v1")]
    [InlineData(UploadFileType.CloudFile, MediaKind.Video, true, "previews-v1")]
    public void ResolveWriteProfile_RoutesEverySupportedObjectType(
        UploadFileType fileType,
        MediaKind mediaKind,
        bool isPreview,
        string expectedProfileId)
    {
        using var registry = new S3BucketRegistry(Configuration(
            Profile("universal-v1", "universal", "https://local.example", "universal", true),
            Profile("avatars-v1", "avatars", "https://local.example", "avatars", true),
            Profile("images-v1", "images", "https://local.example", "images", true),
            Profile("videos-v1", "videos", "https://local.example", "videos", true),
            Profile("audio-v1", "audio", "https://local.example", "audio", true),
            Profile("documents-v1", "documents", "https://local.example", "documents", true),
            Profile("other-v1", "other", "https://local.example", "other", true),
            Profile("previews-v1", "previews", "https://local.example", "previews", true)));

        registry.ResolveWriteProfileId(fileType, mediaKind, isPreview)
            .Should().Be(expectedProfileId);
    }

    private static IConfiguration Configuration(params Dictionary<string, string?>[] profiles)
    {
        var values = profiles.SelectMany(profile => profile).ToDictionary(item => item.Key, item => item.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Dictionary<string, string?> Profile(
        string id,
        string role,
        string endpoint,
        string bucket,
        bool active,
        bool isLegacy = false) => new()
        {
            [$"StorageProfiles:{id}:ProfileId"] = id,
            [$"StorageProfiles:{id}:Role"] = role,
            [$"StorageProfiles:{id}:Version"] = "1",
            [$"StorageProfiles:{id}:ServiceUrl"] = endpoint,
            [$"StorageProfiles:{id}:AccessKey"] = "access",
            [$"StorageProfiles:{id}:SecretKey"] = "secret",
            [$"StorageProfiles:{id}:BucketName"] = bucket,
            [$"StorageProfiles:{id}:IsR2"] = "false",
            [$"StorageProfiles:{id}:IsActive"] = active.ToString(),
            [$"StorageProfiles:{id}:IsLegacy"] = isLegacy.ToString()
        };
}
