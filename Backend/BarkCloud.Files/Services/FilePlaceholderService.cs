using System.Text.Json;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace BarkCloud.Files.Services;

public sealed class FilePlaceholderService(
    FilesContext context, S3Uploader s3, S3BucketRegistry registry, ImagePlaceholderSampler sampler)
{
    /// <summary>Новые загрузки используют готовые JPEG-байты; бэкафилл читает только превью.</summary>
    public async Task<bool> EnsureAsync(
        UploadFile file,
        IReadOnlyDictionary<Guid, byte[]>? previewBytes = null,
        bool overwriteExisting = false,
        CancellationToken cancellationToken = default)
    {
        if (file.Type != UploadFileType.CloudFile || file.MediaKind is not (MediaKind.Photo or MediaKind.Video))
            return false;

        var isPhoto = file.MediaKind == MediaKind.Photo;
        var source = await context.FilePreviews.AsNoTracking()
            .Where(p => p.OriginalFileId == file.Id && (p.TargetWidth > 0 || (isPhoto && p.TargetWidth == 0)))
            .Where(p => context.UploadedFiles.WhereReady().Any(f => f.Id == p.PreviewFileId))
            .OrderBy(p => p.TargetWidth == 0).ThenBy(p => p.TargetWidth)
            .FirstOrDefaultAsync(cancellationToken);
        if (source is null)
            return false;

        var existing = await context.FilePlaceholders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.FileId == file.Id, cancellationToken);
        if (existing is not null && (!overwriteExisting || existing.SourceFilePreviewId == source.Id))
            return false;

        byte[] bytes;
        if (previewBytes is not null)
        {
            // Байты принадлежат конкретному неизменяемому блобу, а не просто той же ширине.
            // Другой пайплайн мог заменить обложку ещё до выбора источника выше.
            if (!previewBytes.TryGetValue(source.PreviewFileId, out var supplied))
                return false;
            bytes = supplied;
        }
        else
        {
            var previewFile = await context.UploadedFiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == source.PreviewFileId, cancellationToken);
            if (previewFile is null || !previewFile.IsReady())
                return false;

            await using var input = await s3.DownloadAsync(
                registry.ResolveReadProfileId(previewFile), previewFile.Id.ToString(), cancellationToken);
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        var sample = await sampler.SampleAsync(bytes, isPhoto, cancellationToken);
        object colors = context.Database.IsNpgsql() ? sample.Colors : JsonSerializer.Serialize(sample.Colors);

        // Повторно проверяем источник в самом INSERT: обложка могла смениться во время чтения S3.
        var sql = """
            INSERT INTO "FilePlaceholders" ("FileId", "SourceFilePreviewId", "Colors", "AspectRatio")
            SELECT {0}, {1}, {2}, {3}
            WHERE EXISTS (SELECT 1 FROM "UploadedFiles" WHERE "Id" = {0})
              AND {1} = (
                SELECT "Id" FROM "FilePreviews"
                WHERE "OriginalFileId" = {0} AND ("TargetWidth" > 0 OR ({4} AND "TargetWidth" = 0))
                  AND EXISTS (
                    SELECT 1 FROM "UploadedFiles" AS preview
                    WHERE preview."Id" = "FilePreviews"."PreviewFileId"
                      AND preview."UploadedAt" IS NOT NULL AND preview."Etag" IS NOT NULL AND preview."Etag" <> ''
                  )
                ORDER BY ("TargetWidth" = 0), "TargetWidth" LIMIT 1
              )
            ON CONFLICT ("FileId")
            """ + (overwriteExisting
            ? " DO UPDATE SET \"SourceFilePreviewId\" = EXCLUDED.\"SourceFilePreviewId\", \"Colors\" = EXCLUDED.\"Colors\", \"AspectRatio\" = EXCLUDED.\"AspectRatio\""
            : " DO NOTHING");

        try
        {
            return await context.Database.ExecuteSqlRawAsync(
                sql, [file.Id, source.Id, colors, sample.AspectRatio, isPhoto], cancellationToken) > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // Оригинал или исходное превью удалены после проверки, но до вставки.
            return false;
        }
    }
}
