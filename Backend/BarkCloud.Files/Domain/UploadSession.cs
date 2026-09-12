using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

public enum UploadSessionStatus
{
    Uploading = 1,
    Processing = 2,
    Ready = 3,
    Failed = 4,
    Cancelled = 5,
    Expired = 6
}

public sealed class UploadSession
{
    [Key]
    public Guid Id { get; set; }

    public Guid FileId { get; set; }

    public long OwnerId { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long DeclaredSize { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public UploadSessionStatus Status { get; set; }

    public string StorageProfileId { get; set; } = string.Empty;

    public string MultipartUploadId { get; set; } = string.Empty;

    public long PartSize { get; set; }

    public string UploadTokenHash { get; set; } = string.Empty;

    public long ReservedBytes { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime LastActivityAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public string? CompletedEtag { get; set; }

    public DateTime? LegacyProcessingCompletedAt { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public int ProcessingAttempts { get; set; }

    public bool CleanupPending { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
