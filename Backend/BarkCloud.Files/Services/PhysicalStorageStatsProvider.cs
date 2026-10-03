using System.Diagnostics;

namespace BarkCloud.Files.Services;

public sealed class PhysicalStorageStatsProvider : IPhysicalStorageStatsProvider
{
    private const string DefaultStoragePath = "/mnt/minio-data";
    private readonly IConfiguration _configuration;
    private readonly ILogger<PhysicalStorageStatsProvider> _logger;
    private readonly StorageStatsCache<PhysicalStorageStats> _cache;

    public PhysicalStorageStatsProvider(
        IConfiguration configuration,
        ILogger<PhysicalStorageStatsProvider> logger,
        IHostApplicationLifetime? lifetime = null,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _logger = logger;
        _cache = new(token => Task.FromResult(CalculateStats(token)), timeProvider ?? TimeProvider.System,
            logger, lifetime?.ApplicationStopping ?? CancellationToken.None);
    }

    public Task<PhysicalStorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
        => GetAsync(_cache.HasValue, cancellationToken);

    public Task<PhysicalStorageStats> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => GetAsync(true, cancellationToken);

    private async Task<PhysicalStorageStats> GetAsync(bool nonBlocking, CancellationToken cancellationToken)
    {
        var snapshot = await _cache.GetAsync(nonBlocking, cancellationToken);
        return (snapshot.Value ?? new PhysicalStorageStats(0, 0, 0, 0)) with
        {
            State = snapshot.State,
            UpdatedAt = snapshot.UpdatedAt
        };
    }

    private PhysicalStorageStats CalculateStats(CancellationToken cancellationToken)
    {
        var storagePath = _configuration["StorageProbe:Path"];
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            storagePath = DefaultStoragePath;
        }

        var fullPath = Path.GetFullPath(storagePath);
        var sw = Stopwatch.StartNew();

        var drive = ResolveDrive(fullPath);
        var s3UsedBytes = Directory.Exists(fullPath)
            ? CalculateDirectorySize(fullPath, cancellationToken)
            : 0;

        if (!Directory.Exists(fullPath))
        {
            _logger.LogWarning(
                "Storage probe path {StoragePath} does not exist. S3 used storage will be reported as 0.",
                fullPath);
        }

        var diskUsedBytes = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
        var diskUsedWithoutS3Bytes = Math.Max(0, diskUsedBytes - s3UsedBytes);

        _logger.LogInformation(
            "Storage probe refreshed. Path: {StoragePath}, Total: {TotalBytes}, Free: {FreeBytes}, S3: {S3Bytes}, Other: {OtherBytes}, ElapsedMs: {ElapsedMs}",
            fullPath,
            drive.TotalSize,
            drive.AvailableFreeSpace,
            s3UsedBytes,
            diskUsedWithoutS3Bytes,
            sw.ElapsedMilliseconds);

        return new PhysicalStorageStats(
            drive.TotalSize,
            drive.AvailableFreeSpace,
            diskUsedWithoutS3Bytes,
            s3UsedBytes);
    }

    private long CalculateDirectorySize(string rootPath, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false
        };

        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Storage probe skipped file {File}", file);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogDebug(ex, "Storage probe cannot access file {File}", file);
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Storage probe failed while reading {StoragePath}", rootPath);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Storage probe cannot access {StoragePath}", rootPath);
            throw;
        }

        return total;
    }

    private static DriveInfo ResolveDrive(string path)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var drive = DriveInfo.GetDrives()
            .Where(d => normalizedPath.StartsWith(Path.TrimEndingDirectorySeparator(d.Name), comparison))
            .OrderByDescending(d => d.Name.Length)
            .FirstOrDefault();

        return drive ?? new DriveInfo(Path.GetPathRoot(path) ?? path);
    }
}
