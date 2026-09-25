using Amazon.Runtime;
using Amazon.S3;

using BarkCloud.Files.Configurations;
using BarkCloud.Files.Domain;

using System.Security.Cryptography;
using System.Text;

namespace BarkCloud.Files.Infrastructure;

/// <summary>
/// Immutable startup registry of versioned storage profiles. A file always addresses a profile id,
/// never a physical bucket name, so equal bucket names at different endpoints remain unambiguous.
/// </summary>
public class S3BucketRegistry : IDisposable
{
    public const string UniversalRole = "universal";
    public const string UserAvatarsOldProfileId = "user-avatars-old-v1";
    public const string CloudFilesOldProfileId = "cloud-files-old-v1";

    private readonly Dictionary<string, StorageProfileOptions> _profiles;
    private readonly Dictionary<string, string> _activeProfileByRole;
    private readonly Dictionary<string, IAmazonS3> _clientsByProfileId;
    private readonly List<IAmazonS3> _uniqueClients;

    public S3BucketRegistry(IConfiguration configuration)
    {
        _profiles = new Dictionary<string, StorageProfileOptions>(StringComparer.Ordinal);
        _activeProfileByRole = new Dictionary<string, string>(StringComparer.Ordinal);
        _clientsByProfileId = new Dictionary<string, IAmazonS3>(StringComparer.Ordinal);

        foreach (var section in configuration.GetSection("StorageProfiles").GetChildren())
        {
            var profile = new StorageProfileOptions();
            section.Bind(profile);
            if (string.IsNullOrWhiteSpace(profile.ProfileId))
                profile.ProfileId = section.Key;
            Validate(profile);
            _profiles.Add(profile.ProfileId, profile);
            if (profile.IsActive && !profile.IsLegacy)
            {
                if (!_activeProfileByRole.TryAdd(profile.Role, profile.ProfileId))
                    throw new InvalidOperationException($"More than one active S3 profile is configured for role '{profile.Role}'.");
            }
        }

        AddLegacyConfigurationFallback(configuration, "user-avatars", UserAvatarsOldProfileId, "user-avatars-old");
        AddLegacyConfigurationFallback(configuration, "cloud-files", CloudFilesOldProfileId, "cloud-files-old");

        var clientCache = new Dictionary<string, IAmazonS3>(StringComparer.Ordinal);
        foreach (var profile in _profiles.Values)
        {
            var clientKey = ClientKey(profile);
            if (!clientCache.TryGetValue(clientKey, out var client))
            {
                client = CreateClient(profile);
                clientCache.Add(clientKey, client);
            }
            _clientsByProfileId.Add(profile.ProfileId, client);
        }
        _uniqueClients = clientCache.Values.ToList();
    }

    public virtual string ResolveWriteProfileId(UploadFileType fileType, MediaKind mediaKind, bool isPreview)
    {
        var role = isPreview ? "previews" : fileType switch
        {
            UploadFileType.UserAvatar => "avatars",
            UploadFileType.CloudFile => mediaKind switch
            {
                MediaKind.Photo => "images",
                MediaKind.Video => "videos",
                MediaKind.Audio => "audio",
                MediaKind.Document => "documents",
                _ => "other"
            },
            _ => "other"
        };
        if (_activeProfileByRole.TryGetValue(role, out var specialized))
            return specialized;
        if (_activeProfileByRole.TryGetValue(UniversalRole, out var universal))
            return universal;
        throw new InvalidOperationException($"No active S3 profile is configured for role '{role}' or fallback '{UniversalRole}'.");
    }

    public virtual StorageProfileOptions GetProfile(string profileId) =>
        _profiles.TryGetValue(profileId, out var profile)
            ? profile
            : throw new InvalidOperationException($"S3 profile '{profileId}' is not configured.");

    public virtual string ResolveReadProfileId(UploadFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.StorageProfileId))
            return file.StorageProfileId;

        var legacyProfileId = file.Type == UploadFileType.UserAvatar
            ? UserAvatarsOldProfileId
            : CloudFilesOldProfileId;
        return _profiles.ContainsKey(legacyProfileId)
            ? legacyProfileId
            : ResolveWriteProfileId(file.Type, file.MediaKind, isPreview: false);
    }

    public virtual IAmazonS3 GetClientForProfile(string profileId) =>
        _clientsByProfileId.TryGetValue(profileId, out var client)
            ? client
            : throw new InvalidOperationException($"S3 client is not configured for profile '{profileId}'.");

    public virtual IEnumerable<(StorageProfileOptions Profile, IAmazonS3 Client)> GetAllProfiles() =>
        _profiles.Values.Select(profile => (profile, _clientsByProfileId[profile.ProfileId]));

    // Compatibility surface for old tests/callers while all production paths migrate to ProfileId.
    public virtual string GetBucketName(UploadFileType fileType)
    {
        var profileId = fileType == UploadFileType.UserAvatar
            ? _profiles.ContainsKey(UserAvatarsOldProfileId) ? UserAvatarsOldProfileId : ResolveWriteProfileId(fileType, MediaKind.Photo, false)
            : _profiles.ContainsKey(CloudFilesOldProfileId) ? CloudFilesOldProfileId : ResolveWriteProfileId(fileType, MediaKind.Other, false);
        return GetProfile(profileId).BucketName;
    }

    public IAmazonS3 GetClientForBucket(string bucketName)
    {
        var matches = _profiles.Values.Where(profile => profile.BucketName == bucketName).ToArray();
        return matches.Length switch
        {
            1 => GetClientForProfile(matches[0].ProfileId),
            0 => throw new InvalidOperationException($"S3 client is not configured for bucket '{bucketName}'."),
            _ => throw new InvalidOperationException(
                $"Bucket name '{bucketName}' exists in multiple S3 profiles; address it by ProfileId.")
        };
    }

    public void Dispose()
    {
        foreach (var client in _uniqueClients)
            client.Dispose();
    }

    private void AddLegacyConfigurationFallback(
        IConfiguration configuration,
        string bucketId,
        string profileId,
        string role)
    {
        if (_profiles.ContainsKey(profileId))
            return;
        var section = configuration.GetSection($"S3Buckets:{bucketId}");
        if (!section.Exists())
            return;
        var profile = new StorageProfileOptions
        {
            ProfileId = profileId,
            Role = role,
            Version = 1,
            ServiceUrl = section["ServiceUrl"] ?? string.Empty,
            AccessKey = section["AccessKey"] ?? string.Empty,
            SecretKey = section["SecretKey"] ?? string.Empty,
            BucketName = section["BucketName"] ?? bucketId,
            IsLegacy = true,
            IsActive = false,
            IsR2 = (section["ServiceUrl"] ?? string.Empty).Contains(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase)
        };
        Validate(profile);
        _profiles.Add(profile.ProfileId, profile);
    }

    private static IAmazonS3 CreateClient(StorageProfileOptions profile)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = R2Endpoint(profile),
            ForcePathStyle = true
        };
        if (profile.IsR2)
        {
            config.AuthenticationRegion = "auto";
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        return new AmazonS3Client(new BasicAWSCredentials(profile.AccessKey, profile.SecretKey), config);
    }

    private static string R2Endpoint(StorageProfileOptions profile)
    {
        if (!profile.IsR2)
            return profile.ServiceUrl;
        var uri = new UriBuilder(profile.ServiceUrl) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        return uri.Uri.ToString().TrimEnd('/');
    }

    private static string ClientKey(StorageProfileOptions profile)
    {
        var value = $"{R2Endpoint(profile)}|{profile.AccessKey}|{profile.SecretKey}|{profile.IsR2}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static void Validate(StorageProfileOptions profile)
    {
        if (new[] { profile.ProfileId, profile.Role, profile.ServiceUrl, profile.AccessKey, profile.SecretKey, profile.BucketName }
            .Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"S3 profile '{profile.ProfileId}' is incomplete.");
        if (profile.QuotaBytes < 0)
            throw new InvalidOperationException($"S3 profile '{profile.ProfileId}' has a negative quota.");
        if (!Uri.TryCreate(profile.ServiceUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"S3 profile '{profile.ProfileId}' has an invalid endpoint.");
    }
}
