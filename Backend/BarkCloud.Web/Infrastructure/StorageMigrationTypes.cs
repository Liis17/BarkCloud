using BarkCloud.Proto.Configuration;

namespace BarkCloud.Web.Infrastructure;

public sealed record MigrationConnection(string ServiceUrl, string AccessKey, string SecretKey,
    string BucketName, bool IsR2 = false, string Region = "", bool ForcePathStyle = true)
{
    public MigrationLocation Location => new(ServiceUrl, BucketName, IsR2, Region, ForcePathStyle);
    public override string ToString() => $"{ServiceUrl} / {BucketName}";
    public StorageProfileItem ToProto() => new()
    {
        ServiceUrl = ServiceUrl, AccessKey = AccessKey, SecretKey = SecretKey,
        BucketName = BucketName, IsR2 = IsR2, Region = Region, ForcePathStyle = ForcePathStyle
    };
    public static MigrationConnection FromProfile(StorageProfileItem profile) => new(
        profile.ServiceUrl, profile.AccessKey, profile.SecretKey, profile.BucketName, profile.IsR2,
        profile.Region, !profile.HasForcePathStyle || profile.ForcePathStyle);
}

public sealed record MigrationLocation(string ServiceUrl, string BucketName, bool IsR2, string Region, bool ForcePathStyle);
public sealed record MigrationSource(string Id, string ServiceUrl, string BucketName, string[] Roles, string[] ProfileIds);
public sealed record MigrationCheckResult(string ValidationId, string Message);
public sealed record MigrationCutover(string Id, string State, MigrationSource Source, MigrationLocation Destination,
    int ActiveUploads, bool CanCancel, bool CanRecover);
public sealed record MigrationObject(string Key, long Size, string Etag, DateTime? LastModified, string? Sha256 = null, string? MetadataHash = null, string? CopyReason = null);
public sealed record MigrationExistingCheck(MigrationObject? Verified, string? CopyReason);
public sealed record MigrationProgress(long UploadedBytes, string Phase, string? FileName = null);
public sealed class MigrationSourceMissingException : Exception;
public sealed record MigrationJobSnapshot(string Id, string State, string Phase, MigrationSource Source,
    MigrationLocation Destination, string TotalBytes, string CopiedBytes, string CurrentBytes,
    long TotalFiles, long CopiedFiles, string? CurrentKey, string? CurrentName,
    string? Error, int ActiveUploads, bool CanCancel, bool CanRetry, bool CanApply, DateTime CreatedAt,
    long SkippedFiles = 0, long UploadedFiles = 0, string? CurrentReason = null);

internal sealed class StorageMigrationJob(MigrationSource source, MigrationConnection sourceConnection,
    MigrationConnection destination, string actor, string workDirectory, string? id = null)
{
    public string Id { get; } = id ?? Guid.NewGuid().ToString();
    public MigrationSource Source { get; } = source;
    public MigrationConnection SourceConnection { get; } = sourceConnection;
    public MigrationConnection Destination { get; } = destination;
    public string Actor { get; } = actor;
    public string Directory { get; } = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public object Sync { get; } = new();
    public CancellationTokenSource Cancellation { get; set; } = new();
    public string State { get; set; } = "queued";
    public string Phase { get; set; } = "counting";
    public string Operation { get; set; } = "copy";
    public bool InventoryComplete { get; set; }
    public bool ExistingScanComplete { get; set; }
    public long ScannedObjects { get; set; }
    public long InspectedManifestLength { get; set; }
    public bool FinalInventoryComplete { get; set; }
    public long ProcessedObjects { get; set; }
    public long ConfirmedManifestLength { get; set; }
    public bool BarrierStarted { get; set; }
    public bool FinalSyncDone { get; set; }
    public bool ConfigurationApplied { get; set; }
    public bool PointOfNoReturn { get; set; }
    public long TotalBytes { get; set; }
    public long CopiedBytes { get; set; }
    public long CurrentBytes { get; set; }
    public long TotalFiles { get; set; }
    public long CopiedFiles { get; set; }
    public long SkippedFiles { get; set; }
    public long UploadedFiles { get; set; }
    public long BaseCopiedBytes { get; set; }
    public long BaseCopiedFiles { get; set; }
    public long BaseTotalBytes { get; set; }
    public long BaseTotalFiles { get; set; }
    public string? CurrentKey { get; set; }
    public string? CurrentName { get; set; }
    public string? CurrentReason { get; set; }
    public string? Error { get; set; }
    public int ActiveUploads { get; set; }
    public void Update(Action update) { lock (Sync) update(); }
    public MigrationJobSnapshot Snapshot()
    {
        lock (Sync) return new(Id, State, Phase, Source, Destination.Location,
            TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CopiedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CurrentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), TotalFiles, CopiedFiles,
            CurrentKey, CurrentName, Error, ActiveUploads,
            (!PointOfNoReturn || State == "failed" && !ConfigurationApplied) && State is not ("completed" or "cancelled"), State == "failed",
            State == "copied", CreatedAt, SkippedFiles, UploadedFiles, CurrentReason);
    }
}
