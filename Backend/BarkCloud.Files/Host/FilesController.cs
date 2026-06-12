using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Features.DownloadFile;
using BarkCloud.Files.Features.UploadFile;
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

    public FilesController(IMediator mediator, MetricsCollector metrics)
    {
        _mediator = mediator;
        _metrics = metrics;
    }

    [HttpPost("upload/{uploadId}")]
    [RequestSizeLimit(536_870_912)]
    [RequestFormLimits(MultipartBodyLengthLimit = 536_870_912)]
    public async Task<IActionResult> UploadFile([FromRoute] Guid uploadId, [FromForm] IFormFile? file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("Файл не выбран или пустой.");
        }

        using var command = new UploadFileCommand()
        {
            FileId = uploadId,
            FileStream = file.OpenReadStream(),
            FileName = file.FileName,
            FileSize = file.Length
        };

        try
        {
            var resultFileId = await _mediator.Send(command);
            _metrics.Increment("files_uploaded");
            _metrics.Add("upload_bytes_total", file.Length);
            return Ok(new { fileId = resultFileId });
        }
        catch (FileAlreadyUploadedException ex)
        {
            return BadRequest(ex.Message);
        }
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
