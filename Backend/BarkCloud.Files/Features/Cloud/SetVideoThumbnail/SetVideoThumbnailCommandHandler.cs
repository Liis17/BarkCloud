using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using Microsoft.EntityFrameworkCore;

using FileNotFoundException = BarkCloud.Shared.Exceptions.Files.FileNotFoundException;
using MediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Features.Cloud.SetVideoThumbnail;

/// <summary>
/// Ручная смена превью видео: берёт загруженную пользователем картинку, генерирует из неё
/// набор превью {1024,512,128} и заменяет существующие FilePreview видео.
/// </summary>
public class SetVideoThumbnailCommandHandler : IRequestHandler<SetVideoThumbnailCommand, CloudEmpty>
{
    private static readonly int[] CloudPreviewWidths = { 1024, 512, 128 };

    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ImageCompressor _imageCompressor;
    private readonly PreviewPersistenceService _previewPersistence;
    private readonly S3Uploader _s3Uploader;
    private readonly S3BucketRegistry _bucketRegistry;
    private readonly FilesContext _context;
    private readonly UserContext _userContext;
    private readonly ILogger<SetVideoThumbnailCommandHandler> _logger;

    public SetVideoThumbnailCommandHandler(
        IUploadedFilesStorage filesStorage,
        ImageCompressor imageCompressor,
        PreviewPersistenceService previewPersistence,
        S3Uploader s3Uploader,
        S3BucketRegistry bucketRegistry,
        FilesContext context,
        UserContext userContext,
        ILogger<SetVideoThumbnailCommandHandler> logger)
    {
        _filesStorage = filesStorage;
        _imageCompressor = imageCompressor;
        _previewPersistence = previewPersistence;
        _s3Uploader = s3Uploader;
        _bucketRegistry = bucketRegistry;
        _context = context;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(SetVideoThumbnailCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var video = await _filesStorage.GetFile(request.VideoFileId);
        var source = await _filesStorage.GetFile(request.SourceImageFileId);
        if (video is null || source is null)
            throw new FileNotFoundException();

        // Оба файла должны принадлежать пользователю.
        if (!video.Uploaders.Contains(ownerId) || !source.Uploaders.Contains(ownerId))
            throw new CloudAccessDeniedException();

        // Видео-файл и картинка-источник.
        if (video.MediaKind != MediaKind.Video || source.MediaKind != MediaKind.Photo)
            throw new InvalidThumbnailSourceException();

        var bucketName = _bucketRegistry.GetBucketName(video.Type);

        // 1) Скачиваем картинку-источник в память (S3-поток не seekable) и генерируем превью.
        List<MultiPreviewItem> previews;
        await using (var s3Stream = await _s3Uploader.DownloadAsync(_bucketRegistry.GetBucketName(source.Type), source.Id.ToString()))
        {
            using var memStream = new MemoryStream();
            await s3Stream.CopyToAsync(memStream, cancellationToken);
            memStream.Position = 0;
            previews = await _imageCompressor.GenerateMultiplePreviewsAsync(memStream, CloudPreviewWidths, cancellationToken);
        }

        // 2) Снимаем старые превью видео: убираем владельца из их Uploaders и удаляем связки.
        var oldPreviews = await _context.FilePreviews
            .Where(p => p.OriginalFileId == video.Id)
            .ToListAsync(cancellationToken);

        if (oldPreviews.Count > 0)
        {
            var oldPreviewFileIds = oldPreviews.Select(p => p.PreviewFileId).ToList();
            var oldPreviewFiles = await _context.UploadedFiles
                .Where(f => oldPreviewFileIds.Contains(f.Id))
                .ToListAsync(cancellationToken);

            foreach (var pf in oldPreviewFiles)
                pf.Uploaders.Remove(ownerId);

            _context.FilePreviews.RemoveRange(oldPreviews);
            await _context.SaveChangesAsync(cancellationToken);
        }

        // 3) Сохраняем новые превью (дедуп + S3 + связки) той же логикой, что при загрузке.
        await _previewPersistence.PersistPreviewsAsync(video, previews, bucketName, cancellationToken);

        _logger.LogInformation(
            "Превью видео {VideoId} заменено из картинки {SourceId} (Owner: {OwnerId}, превью: {Count})",
            video.Id, source.Id, ownerId, previews.Count);

        return new CloudEmpty();
    }
}
