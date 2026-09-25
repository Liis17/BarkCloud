namespace BarkCloud.Configuration.Domain;

public sealed class StorageProfile
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

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string CreatedFrom { get; set; } = string.Empty;

    public DateTime EditedAt { get; set; }

    public string EditedBy { get; set; } = string.Empty;

    public string EditedFrom { get; set; } = string.Empty;
}
