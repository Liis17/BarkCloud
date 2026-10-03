using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.Metrics;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Net.Http.Headers;

namespace BarkCloud.Files.Host;

/// <summary>
/// Допуск legacy-загрузки <c>POST /upload/{uploadId}</c> до чтения тела. Resource-фильтр выполняется
/// раньше model binding, поэтому multipart не буферизуется на диск, пока не проверены разрешение,
/// размер и бюджет ресурсов.
/// </summary>
public sealed class LegacyUploadAdmissionFilter(
    LegacyUploadQuotaGuard guard,
    LegacyUploadBudget budget,
    LegacyUploadOptions options,
    MetricsCollector metrics,
    ILogger<LegacyUploadAdmissionFilter> logger) : IAsyncResourceFilter
{
    private const int RetryAfterSeconds = 30;
    public const string MigrationActivityKey = "barkcloud.migration-activity";

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var request = http.Request;
        var contentLength = request.ContentLength;

        if (!context.RouteData.Values.TryGetValue("uploadId", out var rawId)
            || !Guid.TryParse(rawId?.ToString(), out var uploadId))
        {
            Reject(context, "not_found", null, contentLength, new NotFoundResult());
            return;
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            Reject(context, "unsupported_media_type", uploadId, contentLength, new ObjectResult(new
            {
                error = "Загрузка принимается только как multipart/form-data."
            })
            {
                StatusCode = StatusCodes.Status415UnsupportedMediaType
            });
            return;
        }

        LegacyUploadAdmission admission;
        try
        {
            admission = await guard.AdmitAsync(uploadId, contentLength, http.RequestAborted);
        }
        catch (BarkCloud.Shared.Exceptions.Files.FileNotFoundException ex)
        {
            Reject(context, "not_found", uploadId, contentLength,
                new NotFoundObjectResult(new { error = ex.ErrorMessage, code = ex.ErrorCode }));
            return;
        }
        catch (BarkCloud.Shared.Exceptions.Files.UploadQuotaExceededException ex)
        {
            metrics.Increment("upload_quota_rejections_total");
            Reject(context, "quota", uploadId, contentLength, new ConflictObjectResult(new
            {
                error = ex.ErrorMessage,
                code = ex.ErrorCode,
                limit = ex.LimitBytes,
                used = ex.UsedBytes,
                reserved = ex.ReservedBytes,
                requested = ex.RequestedBytes
            }));
            return;
        }
        catch (FileAlreadyUploadedException ex)
        {
            Reject(context, "already_uploaded", uploadId, contentLength, new BadRequestObjectResult(ex.Message));
            return;
        }
        catch (BarkCloud.Shared.Exceptions.Files.StorageMigrationPausedException ex)
        {
            Reject(context, "migration", uploadId, contentLength,
                new ConflictObjectResult(new { error = ex.ErrorMessage, code = ex.ErrorCode }));
            return;
        }

        await using var migrationActivity = admission.Activity;
        if (migrationActivity is not null) http.Items[MigrationActivityKey] = migrationActivity.Id;

        if (contentLength is { } length && length - options.MultipartOverheadBytes > admission.MaxBytes)
        {
            Reject(context, "too_large", uploadId, contentLength, new ObjectResult(new
            {
                error = "Файл превышает допустимый размер загрузки.",
                code = "legacy_upload_too_large",
                limit = admission.MaxBytes
            })
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge
            });
            return;
        }

        // При chunked длина неизвестна — резервируем под буфер весь потолок.
        var lease = await budget.TryAcquireAsync(
            contentLength ?? admission.MaxBytes + options.MultipartOverheadBytes, http.RequestAborted);
        if (lease is null)
        {
            http.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
            Reject(context, "busy", uploadId, contentLength, new StatusCodeResult(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        // Не в finally вокруг next(): к OnCompleted временный файл формы уже удалён.
        http.Response.OnCompleted(() =>
        {
            lease.Dispose();
            return Task.CompletedTask;
        });

        var bodySize = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
            bodySize.MaxRequestBodySize = admission.MaxBytes + options.MultipartOverheadBytes;

        // На HTTP/2 и HTTP/3 Kestrel не поддерживает per-request MinDataRate: сеттер бросает NotSupportedException.
        var dataRate = http.Features.Get<IHttpMinRequestBodyDataRateFeature>();
        if (dataRate is not null && !HttpProtocol.IsHttp2(request.Protocol) && !HttpProtocol.IsHttp3(request.Protocol))
            dataRate.MinDataRate = new MinDataRate(options.MinBytesPerSecond, options.MinRateGrace);

        await next();
    }

    private void Reject(
        ResourceExecutingContext context,
        string reason,
        Guid? uploadId,
        long? contentLength,
        IActionResult result)
    {
        // Без Connection: close Kestrel вычитывал бы неотправленное тело.
        context.HttpContext.Response.Headers.Connection = "close";
        context.Result = result;
        metrics.Increment("legacy_upload_rejected_total");
        logger.LogWarning(
            "Legacy upload отклонён до чтения тела: {Reason}, FileId={FileId}, ContentLength={ContentLength}",
            reason, uploadId, contentLength);
    }
}
