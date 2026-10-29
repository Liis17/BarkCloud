using BarkCloud.Files.Configurations;
using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BarkCloud.Files.Services;

internal sealed class VideoVariantWorker : BackgroundService
{
    private static readonly TimeSpan LeaseRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TranscoderPollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 100;
    private const int MaximumAttempts = 3;
    private const int MaximumErrorLength = 2048;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VideoTranscodeLeaseManager _leaseManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly VideoVariantOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VideoVariantWorker> _logger;
    private readonly string _ownerInstanceId;

    public VideoVariantWorker(
        IServiceScopeFactory scopeFactory,
        VideoTranscodeLeaseManager leaseManager,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IOptions<VideoVariantOptions> options,
        TimeProvider timeProvider,
        ILogger<VideoVariantWorker> logger,
        string? ownerInstanceId = null)
    {
        _scopeFactory = scopeFactory;
        _leaseManager = leaseManager;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _ownerInstanceId = ownerInstanceId ?? $"{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Генерация вариантов видео отключена");
            return;
        }

        ValidateOptions();
        Directory.CreateDirectory(_options.WorkDirectory);
        var workDirectory = Path.GetFullPath(_options.WorkDirectory);
        using var transcoder = new VideoTranscoderClient(
            _httpClientFactory.CreateClient(),
            _options.TranscoderUrl,
            _configuration["TRANSCODER_TOKEN"] ?? string.Empty);

        while (!stoppingToken.IsCancellationRequested)
        {
            long? generation;
            try
            {
                generation = await _leaseManager.TryAcquireAsync(_ownerInstanceId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Не удалось получить аренду обработки видео");
                await DelayAsync(LeaseRetryDelay, stoppingToken);
                continue;
            }

            if (generation is null)
            {
                await DelayAsync(LeaseRetryDelay, stoppingToken);
                continue;
            }

            using var ownerTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var heartbeat = MaintainLeaseAsync(generation.Value, ownerTokenSource);
            var retryAfterRelease = false;
            try
            {
                await PrepareTakeoverAsync(transcoder, workDirectory, generation.Value, ownerTokenSource.Token);
                await ProcessQueueAsync(transcoder, workDirectory, generation.Value, ownerTokenSource.Token);
            }
            catch (OperationCanceledException) when (ownerTokenSource.IsCancellationRequested)
            {
                retryAfterRelease = !stoppingToken.IsCancellationRequested;
            }
            catch (Exception exception)
            {
                retryAfterRelease = true;
                _logger.LogError(exception, "Ошибка цикла обработки вариантов видео поколения {Generation}", generation);
            }
            finally
            {
                ownerTokenSource.Cancel();
                try
                {
                    await heartbeat;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Ошибка остановки heartbeat аренды видео");
                }

                try
                {
                    await _leaseManager.ReleaseAsync(_ownerInstanceId, generation.Value);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Не удалось освободить аренду видео поколения {Generation}", generation);
                }
            }

            if (retryAfterRelease && !stoppingToken.IsCancellationRequested)
                await DelayAsync(LeaseRetryDelay, stoppingToken);
        }
    }

    private void ValidateOptions()
    {
        if (!Path.IsPathFullyQualified(_options.WorkDirectory))
            throw new InvalidOperationException("VideoVariants:WorkDirectory должен быть абсолютным путём.");
        if (!Uri.TryCreate(_options.TranscoderUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("VideoVariants:TranscoderUrl должен быть абсолютным URL.");
        if (_options.MaxSourceSizeMb is <= 0 || _options.MaxDurationMinutes is <= 0)
            throw new InvalidOperationException("Лимиты размера и длительности должны быть положительными или пустыми.");
        if (string.IsNullOrWhiteSpace(_configuration["TRANSCODER_TOKEN"]))
            throw new InvalidOperationException("Для обработки видео не задан TRANSCODER_TOKEN.");
    }

    private async Task MaintainLeaseAsync(long generation, CancellationTokenSource ownerTokenSource)
    {
        try
        {
            while (!ownerTokenSource.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ownerTokenSource.Token);
                if (!await _leaseManager.RenewAsync(_ownerInstanceId, generation, ownerTokenSource.Token))
                    throw new VideoTranscodeLeaseLostException();
            }
        }
        catch (OperationCanceledException) when (ownerTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Heartbeat аренды видео завершился; цикл будет остановлен");
            ownerTokenSource.Cancel();
        }
    }

    private async Task PrepareTakeoverAsync(
        VideoTranscoderClient transcoder,
        string workDirectory,
        long generation,
        CancellationToken cancellationToken)
    {
        await transcoder.CancelCurrentJobAsync(cancellationToken);
        await ReconcilePendingIntentsAsync(generation, cancellationToken);
        CleanWorkDirectory(workDirectory);
        _logger.LogInformation("Files получил аренду обработки видео поколения {Generation}", generation);
    }

    private async Task ProcessQueueAsync(
        VideoTranscoderClient transcoder,
        string workDirectory,
        long generation,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ReconcilePendingIntentsAsync(generation, cancellationToken);
            var candidate = await FindCandidateAsync(cancellationToken);
            if (candidate is null)
            {
                await DelayAsync(IdleDelay, cancellationToken);
                continue;
            }

            try
            {
                await ProcessCandidateAsync(candidate, transcoder, workDirectory, generation, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Не удалось обработать видео {FileId}", candidate.File.Id);
                await MarkFailedAsync(candidate.File.Id, generation, exception.Message, cancellationToken);
                await DelayAsync(ErrorDelay, cancellationToken);
            }
        }
    }

    internal async Task<VideoCandidate?> FindCandidateAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        return await context.UploadedFiles
            .AsNoTracking()
            .Where(file => file.Type == UploadFileType.CloudFile
                && file.MediaKind == MediaKind.Video
                && file.Etag != null && file.Etag != string.Empty
                && file.UploadedAt != null
                && file.Uploaders.Count > 0
                && !context.VideoTranscodeStates.Any(state => state.FileId == file.Id
                    && (state.Status == VideoTranscodeStatus.Done
                        || state.Status == VideoTranscodeStatus.Skipped
                        || (state.Status == VideoTranscodeStatus.Failed && state.Attempts >= MaximumAttempts))))
            .OrderByDescending(file => file.CreatedAt)
            .ThenByDescending(file => file.Id)
            .Select(file => new VideoCandidate(
                file,
                context.FileMetadata.AsNoTracking()
                    .Where(metadata => metadata.FileId == file.Id)
                    .Select(metadata => metadata.DurationSeconds)
                    .FirstOrDefault()))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task ProcessCandidateAsync(
        VideoCandidate candidate,
        VideoTranscoderClient transcoder,
        string workDirectory,
        long generation,
        CancellationToken cancellationToken)
    {
        var file = candidate.File;
        var skipReason = VideoVariantFilter.GetSkipReason(
            file.Size, candidate.DurationSeconds, _options.MaxSourceSizeMb, _options.MaxDurationMinutes);
        if (skipReason is not null)
        {
            await UpdateStateAsync(file.Id, generation, state =>
            {
                state.Status = VideoTranscodeStatus.Skipped;
                state.LastError = skipReason;
            }, cancellationToken);
            return;
        }

        await UpdateStateAsync(file.Id, generation, state =>
        {
            state.Status = VideoTranscodeStatus.Pending;
            state.LastError = null;
        }, cancellationToken);

        string? fileDirectory = null;
        try
        {
            var stateAndVariants = await GetVariantProgressAsync(file.Id, cancellationToken);
            var width = file.ImageWidth;
            var height = file.ImageHeight;
            var shortSide = VideoVariantLadder.GetShortSide(width, height);
            string? sourcePath = null;

            if (shortSide == 0)
            {
                fileDirectory = PrepareFileDirectory(workDirectory, file.Id);
                sourcePath = Path.Combine(fileDirectory, "source");
                await DownloadOriginalAsync(file, sourcePath, cancellationToken);
                using var scope = _scopeFactory.CreateScope();
                var probe = await scope.ServiceProvider.GetRequiredService<VideoThumbnailExtractor>()
                    .ProbeAsync(sourcePath, cancellationToken);
                width = probe.Width;
                height = probe.Height;
                shortSide = VideoVariantLadder.GetShortSide(width, height);
                if (shortSide == 0)
                {
                    await UpdateStateAsync(file.Id, generation, state =>
                    {
                        state.Status = VideoTranscodeStatus.Skipped;
                        state.LastError = "Не удалось определить размеры исходного видео.";
                    }, cancellationToken);
                    return;
                }
            }

            var heights = VideoVariantLadder.GetMissingHeights(
                shortSide,
                stateAndVariants.ExistingHeights,
                stateAndVariants.SkippedHeights);
            if (heights.Count == 0)
            {
                await MarkDoneAsync(file.Id, generation, cancellationToken);
                return;
            }

            fileDirectory ??= PrepareFileDirectory(workDirectory, file.Id);
            sourcePath ??= Path.Combine(fileDirectory, "source");
            if (!File.Exists(sourcePath))
                await DownloadOriginalAsync(file, sourcePath, cancellationToken);

            foreach (var targetHeight in heights)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = Path.Combine(fileDirectory, $"{targetHeight}p.webm");
                var relativeInput = $"{file.Id:N}/source";
                var relativeOutput = $"{file.Id:N}/{targetHeight}p.webm";
                await RunTranscodeJobAsync(
                    transcoder, relativeInput, relativeOutput, targetHeight, generation, cancellationToken);

                var variantSize = new FileInfo(outputPath).Length;
                if (VideoVariantLadder.ShouldSkipBySize(file.Size, variantSize))
                {
                    File.Delete(outputPath);
                    await AddSkippedHeightAsync(file.Id, targetHeight, generation, cancellationToken);
                    continue;
                }

                using var probeScope = _scopeFactory.CreateScope();
                var dimensions = await probeScope.ServiceProvider.GetRequiredService<VideoThumbnailExtractor>()
                    .ProbeAsync(outputPath, cancellationToken);
                var profileId = probeScope.ServiceProvider.GetRequiredService<S3BucketRegistry>()
                    .ResolveWriteProfileId(UploadFileType.VideoVariant, MediaKind.Video, isPreview: false);
                var variantFileId = Guid.NewGuid();
                await UploadAndCommitVariantAsync(
                    file, targetHeight, dimensions.Width, dimensions.Height, variantSize,
                    outputPath, variantFileId, profileId, generation, cancellationToken);
            }

            await MarkDoneAsync(file.Id, generation, cancellationToken);
        }
        finally
        {
            if (fileDirectory is not null)
                TryDeleteWorkDirectory(fileDirectory);
        }
    }

    private async Task<(List<int> ExistingHeights, List<int> SkippedHeights)> GetVariantProgressAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        var existingHeights = await context.VideoVariants.AsNoTracking()
            .Where(variant => variant.OriginalFileId == fileId)
            .Select(variant => variant.Height)
            .ToListAsync(cancellationToken);
        var skippedHeights = await context.VideoTranscodeStates.AsNoTracking()
            .Where(state => state.FileId == fileId)
            .Select(state => state.SkippedHeights)
            .FirstOrDefaultAsync(cancellationToken) ?? [];
        return (existingHeights, skippedHeights);
    }

    private async Task DownloadOriginalAsync(UploadFile file, string destination, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<S3BucketRegistry>();
        var uploader = scope.ServiceProvider.GetRequiredService<S3Uploader>();
        var profileId = registry.ResolveReadProfileId(file);
        await using var source = await uploader.DownloadAsync(profileId, file.Id.ToString(), cancellationToken);
        await using var target = new FileStream(destination, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        await source.CopyToAsync(target, cancellationToken);
    }

    private async Task RunTranscodeJobAsync(
        VideoTranscoderClient transcoder,
        string relativeInput,
        string relativeOutput,
        int height,
        long generation,
        CancellationToken cancellationToken)
    {
        string? jobId = null;
        try
        {
            var job = await transcoder.StartJobAsync(relativeInput, relativeOutput, height, generation, cancellationToken);
            jobId = job.Id;
            while (job.Status == "running")
            {
                await Task.Delay(TranscoderPollInterval, cancellationToken);
                job = await transcoder.GetJobAsync(jobId, cancellationToken)
                    ?? throw new InvalidOperationException("Transcoder потерял задание.");
                if (job.Generation != generation)
                    throw new InvalidOperationException("Transcoder вернул задание другого поколения.");
            }

            if (job.Status != "completed")
                throw new InvalidOperationException(job.Error ?? $"Transcoder завершил задание со статусом {job.Status}.");
        }
        catch
        {
            if (jobId is not null)
            {
                try
                {
                    await transcoder.CancelJobAsync(jobId, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Не удалось подтвердить остановку задания Transcoder {JobId}", jobId);
                }
            }

            throw;
        }
    }

    internal async Task UploadAndCommitVariantAsync(
        UploadFile original,
        int targetHeight,
        int width,
        int actualHeight,
        long size,
        string outputPath,
        Guid variantFileId,
        string profileId,
        long generation,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<StorageMigrationGate>();
        var uploader = scope.ServiceProvider.GetRequiredService<S3Uploader>();
        await using var activity = await gate.TrackUploadAsync([profileId], cancellationToken, variantFileId);
        await SaveIntentAsync(original.Id, variantFileId, profileId, generation, cancellationToken);

        try
        {
            string etag;
            await using (var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                etag = await uploader.UploadAsync(profileId, variantFileId.ToString(), stream, "video/webm", cancellationToken);
            }

            var disposition = await CommitVariantAsync(
                original.Id, targetHeight, width, actualHeight, size, etag,
                variantFileId, profileId, generation, cancellationToken);
            if (disposition == CommitDisposition.Committed)
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Не удалось удалить локальный вариант видео {OutputPath}", outputPath);
                }

                return;
            }

            await uploader.DeleteAsync(profileId, variantFileId.ToString(), cancellationToken);
            await ClearIntentAsync(original.Id, variantFileId, generation, cancellationToken);
            File.Delete(outputPath);
            if (disposition == CommitDisposition.OriginalRemoved)
                throw new OriginalVideoRemovedException();
        }
        catch (OriginalVideoRemovedException)
        {
            throw;
        }
        catch
        {
            try
            {
                await uploader.DeleteAsync(profileId, variantFileId.ToString(), cancellationToken);
                await ClearIntentAsync(original.Id, variantFileId, generation, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Объект варианта {VariantFileId} останется до восстановления намерения", variantFileId);
            }

            throw;
        }
    }

    private async Task SaveIntentAsync(
        Guid originalFileId,
        Guid variantFileId,
        string profileId,
        long generation,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await _leaseManager.EnsureCurrentAsync(context, _ownerInstanceId, generation, cancellationToken);
        if ((await context.LockLiveFilesAsync([originalFileId], cancellationToken)).Count == 0)
            throw new OriginalVideoRemovedException();

        var state = await context.VideoTranscodeStates.SingleOrDefaultAsync(x => x.FileId == originalFileId, cancellationToken);
        if (state is null)
        {
            state = new VideoTranscodeState { FileId = originalFileId };
            context.VideoTranscodeStates.Add(state);
        }
        if (state.PendingVariantFileId is not null)
            throw new InvalidOperationException("У видео уже есть незавершённое намерение загрузки варианта.");

        state.PendingVariantFileId = variantFileId;
        state.PendingProfileId = profileId;
        state.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<CommitDisposition> CommitVariantAsync(
        Guid originalFileId,
        int targetHeight,
        int width,
        int actualHeight,
        long size,
        string etag,
        Guid variantFileId,
        string profileId,
        long generation,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await _leaseManager.EnsureCurrentAsync(context, _ownerInstanceId, generation, cancellationToken);
        if ((await context.LockLiveFilesAsync([originalFileId], cancellationToken)).Count == 0)
            return CommitDisposition.OriginalRemoved;

        if (await context.VideoVariants.AnyAsync(
                variant => variant.OriginalFileId == originalFileId && variant.Height == targetHeight,
                cancellationToken))
            return CommitDisposition.AlreadyExists;

        var original = await context.UploadedFiles.SingleAsync(file => file.Id == originalFileId, cancellationToken);
        var state = await context.VideoTranscodeStates.SingleAsync(x => x.FileId == originalFileId, cancellationToken);
        if (state.PendingVariantFileId != variantFileId || state.PendingProfileId != profileId)
            throw new InvalidOperationException("Намерение загрузки варианта изменилось до коммита.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        context.UploadedFiles.Add(new UploadFile
        {
            Id = variantFileId,
            Uploaders = original.Uploaders.ToList(),
            CreatedAt = now,
            UploadedAt = now,
            Etag = etag,
            Type = UploadFileType.VideoVariant,
            StorageProfileId = profileId,
            MediaKind = MediaKind.Video,
            Filename = $"{targetHeight}p.webm",
            Size = size,
            ImageWidth = width,
            ImageHeight = actualHeight
        });
        context.VideoVariants.Add(new VideoVariant
        {
            Id = Guid.NewGuid(),
            OriginalFileId = originalFileId,
            VariantFileId = variantFileId,
            Height = targetHeight,
            Width = width,
            ActualHeight = actualHeight,
            CreatedAt = now
        });
        state.PendingVariantFileId = null;
        state.PendingProfileId = null;
        state.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CommitDisposition.Committed;
    }

    private async Task ReconcilePendingIntentsAsync(long generation, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        var pending = await context.VideoTranscodeStates.AsNoTracking()
            .Where(state => state.PendingVariantFileId != null)
            .OrderBy(state => state.UpdatedAt)
            .Take(BatchSize)
            .Select(state => new PendingIntent(state.FileId, state.PendingVariantFileId!.Value, state.PendingProfileId))
            .ToListAsync(cancellationToken);

        foreach (var intent in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(intent.ProfileId))
                throw new InvalidOperationException($"Для намерения {intent.VariantFileId} не задан S3-профиль.");

            var isRegistered = await context.UploadedFiles.AsNoTracking()
                .AnyAsync(file => file.Id == intent.VariantFileId, cancellationToken);
            if (!isRegistered)
            {
                using var cleanupScope = _scopeFactory.CreateScope();
                var gate = cleanupScope.ServiceProvider.GetRequiredService<StorageMigrationGate>();
                var uploader = cleanupScope.ServiceProvider.GetRequiredService<S3Uploader>();
                await using var activity = await gate.TrackUploadAsync([intent.ProfileId], cancellationToken, intent.VariantFileId);
                await uploader.DeleteAsync(intent.ProfileId, intent.VariantFileId.ToString(), cancellationToken);
            }

            await ClearIntentAsync(intent.FileId, intent.VariantFileId, generation, cancellationToken);
        }
    }

    private async Task ClearIntentAsync(
        Guid originalFileId,
        Guid variantFileId,
        long generation,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await _leaseManager.EnsureCurrentAsync(context, _ownerInstanceId, generation, cancellationToken);
        var state = await context.VideoTranscodeStates.SingleOrDefaultAsync(x => x.FileId == originalFileId, cancellationToken);
        if (state?.PendingVariantFileId == variantFileId)
        {
            state.PendingVariantFileId = null;
            state.PendingProfileId = null;
            state.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private Task<bool> MarkDoneAsync(Guid fileId, long generation, CancellationToken cancellationToken) =>
        UpdateStateAsync(fileId, generation, state =>
        {
            state.Status = VideoTranscodeStatus.Done;
            state.LastError = null;
        }, cancellationToken);

    private Task<bool> AddSkippedHeightAsync(Guid fileId, int height, long generation, CancellationToken cancellationToken) =>
        UpdateStateAsync(fileId, generation, state =>
        {
            if (!state.SkippedHeights.Contains(height))
                state.SkippedHeights.Add(height);
        }, cancellationToken);

    internal Task<bool> MarkFailedAsync(Guid fileId, long generation, string error, CancellationToken cancellationToken) =>
        UpdateStateAsync(fileId, generation, state =>
        {
            state.Status = VideoTranscodeStatus.Failed;
            state.Attempts++;
            state.LastError = TruncateError(error);
        }, cancellationToken);

    private async Task<bool> UpdateStateAsync(
        Guid fileId,
        long generation,
        Action<VideoTranscodeState> update,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await _leaseManager.EnsureCurrentAsync(context, _ownerInstanceId, generation, cancellationToken);
        if ((await context.LockLiveFilesAsync([fileId], cancellationToken)).Count == 0)
            return false;

        var state = await context.VideoTranscodeStates.SingleOrDefaultAsync(x => x.FileId == fileId, cancellationToken);
        if (state is null)
        {
            state = new VideoTranscodeState { FileId = fileId };
            context.VideoTranscodeStates.Add(state);
        }

        update(state);
        state.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private string PrepareFileDirectory(string workDirectory, Guid fileId)
    {
        var path = Path.Combine(workDirectory, fileId.ToString("N"));
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Рабочий каталог видео не может быть символической ссылкой.");

        Directory.CreateDirectory(path);
        return path;
    }

    private void CleanWorkDirectory(string workDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(workDirectory))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;

            Directory.Delete(directory, recursive: true);
        }
    }

    private void TryDeleteWorkDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Не удалось очистить рабочую папку видео {WorkDirectory}", path);
        }
    }

    private static string TruncateError(string error) =>
        error.Length > MaximumErrorLength ? error[..MaximumErrorLength] : error;

    internal sealed record VideoCandidate(UploadFile File, double? DurationSeconds);
    private sealed record PendingIntent(Guid FileId, Guid VariantFileId, string? ProfileId);

    private enum CommitDisposition
    {
        Committed,
        OriginalRemoved,
        AlreadyExists
    }

    private sealed class OriginalVideoRemovedException : InvalidOperationException
    {
        public OriginalVideoRemovedException() : base("Исходное видео удалено или больше не имеет владельцев.")
        {
        }
    }
}
