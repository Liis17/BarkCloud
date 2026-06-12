using System.Diagnostics;

namespace BarkCloud.Files.Services;

public sealed class PhysicalStorageStatsProvider : IPhysicalStorageStatsProvider
{
    private const string DefaultStoragePath = "/mnt/minio-data";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IConfiguration _configuration;
    private readonly ILogger<PhysicalStorageStatsProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private PhysicalStorageStats? _cachedStats;
    private DateTimeOffset _cachedAt;

    public PhysicalStorageStatsProvider(
        IConfiguration configuration,
        ILogger<PhysicalStorageStatsProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PhysicalStorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedStats is not null && now - _cachedAt < CacheDuration)
        {
            return _cachedStats;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedStats is not null && now - _cachedAt < CacheDuration)
            {
                return _cachedStats;
            }

            var stats = CalculateStats(cancellationToken);
            _cachedStats = stats;
            _cachedAt = now;

            return stats;
        }
        finally
        {
            _refreshLock.Release();
        }
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
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Storage probe cannot access {StoragePath}", rootPath);
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
