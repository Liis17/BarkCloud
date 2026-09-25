namespace BarkCloud.Files.Configurations;

public sealed class StorageProfileOptions
{
    public string ProfileId { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public int Version { get; set; }

    public string ServiceUrl { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string BucketName { get; set; } = string.Empty;

    public long QuotaBytes { get; set; }

    public bool IsR2 { get; set; }

    public bool IsActive { get; set; }

    public bool IsLegacy { get; set; }
}
