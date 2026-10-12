using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Features.DownloadFile;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.Metrics;

using MediatR;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace BarkCloud.Files.Host;

public class FilesController : Controller
{
    private readonly IMediator _mediator;
    private readonly MetricsCollector _metrics;
    private readonly UploadSessionCoordinator _uploadSessions;

    private const string LegacyUploadRemovedErrorCode = "legacy_upload_removed";

    public FilesController(
        IMediator mediator,
        MetricsCollector metrics,
        UploadSessionCoordinator uploadSessions)
    {
        _mediator = mediator;
        _metrics = metrics;
        _uploadSessions = uploadSessions;
    }

    [HttpPut("file-upload/{sessionId:guid}/parts/{partNumber:int}")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadSessionPart(
        [FromRoute] Guid sessionId,
        [FromRoute] int partNumber,
        CancellationToken cancellationToken)
    {
        if (Request.ContentType is null
            || !Request.ContentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new
            {
                error = "Части принимаются только как application/octet-stream."
            });
        }

        var token = Request.Headers["X-Upload-Token"].ToString();
        var range = Request.GetTypedHeaders().ContentRange;
        if (range?.From is null || range.To is null || range.Length is null || Request.ContentLength is null)
            return BadRequest(new { error = "Требуется корректный Content-Range." });

        try
        {
            var part = await _uploadSessions.UploadPartAsync(
                sessionId,
                token,
                partNumber,
                range.From.Value,
                range.To.Value,
                range.Length.Value,
                Request.Body,
                Request.ContentLength.Value,
                cancellationToken);
            _metrics.Increment("upload_parts_total");
            _metrics.Add("upload_part_bytes_total", part.Size);
            return Ok(new { partNumber = part.PartNumber, size = part.Size });
        }
        catch (BarkCloud.Shared.Exceptions.Files.UploadSessionNotFoundException ex)
        {
            return NotFound(new { error = ex.ErrorMessage, code = ex.ErrorCode });
        }
        catch (BarkCloud.Shared.Exceptions.Files.UploadTokenInvalidException ex)
        {
            return Unauthorized(new { error = ex.ErrorMessage, code = ex.ErrorCode });
        }
        catch (BarkCloud.Shared.Exceptions.BaseGrpcException ex)
        {
            return Conflict(new { error = ex.ErrorMessage, code = ex.ErrorCode });
        }
    }

    [HttpPost("upload/{uploadId:guid}")]
    public IActionResult UploadFile()
    {
        if (Request.Protocol.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
            Response.Headers.Connection = "close";

        return StatusCode(StatusCodes.Status418ImATeapot, new
        {
            error = "Загрузка через этот адрес больше не поддерживается, используйте Upload 2.0 (CreateUploadSession).",
            code = LegacyUploadRemovedErrorCode
        });
    }

    [HttpGet("download/{fileId}")]
    public async Task<IActionResult> DownloadFile([FromRoute] Guid fileId, CancellationToken cancellationToken)
    {
        DownloadFileResult result;
        try
        {
            var command = new DownloadFileCommand()
            {
                FileId = fileId
            };

            // Один диапазон вида bytes=from-[to]. Multi-range и suffix (bytes=-N) не поддерживаем —
            // отдаём файл целиком (наш клиент шлёт только явные from-to).
            var range = Request.GetTypedHeaders().Range;
            if (range?.Ranges.Count == 1)
            {
                var item = range.Ranges.First();
                if (item.From.HasValue)
                {
                    command.RangeStart = item.From.Value;
                    command.RangeEnd = item.To;
                }
            }

            result = await _mediator.Send(command);
        }
        catch (FileNotUploadedException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return NotFound($"Ошибка при скачивании файла: {ex.Message}");
        }

        Response.Headers.AcceptRanges = "bytes";
        Response.ContentType = result.ContentType;
        var contentDisposition = new ContentDispositionHeaderValue("attachment");
        contentDisposition.SetHttpFileName(result.FileName);
        Response.Headers.ContentDisposition = contentDisposition.ToString();

        if (result.IsPartial)
        {
            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.Headers.ContentRange = $"bytes {result.RangeStart}-{result.RangeEnd}/{result.TotalSize}";
            Response.ContentLength = result.ContentLength;
        }

        await using (result.FileStream)
        {
            await result.FileStream.CopyToAsync(Response.Body, cancellationToken);
        }

        return new EmptyResult();
    }
}
