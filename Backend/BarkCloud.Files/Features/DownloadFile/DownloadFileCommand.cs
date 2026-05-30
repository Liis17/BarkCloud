using MediatR;

namespace BarkCloud.Files.Features.DownloadFile;

public class DownloadFileCommand : IRequest<DownloadFileResult>
{
    public Guid FileId { get; set; }

    // Запрошенный диапазон байтов (HTTP Range). RangeStart == null → файл целиком.
    // RangeEnd == null при заданном RangeStart → «от start до конца файла».
    public long? RangeStart { get; set; }
    public long? RangeEnd { get; set; }
}

public class DownloadFileResult
{
    public Stream FileStream { get; set; }
    public string FileName { get; set; }
    public string ContentType { get; set; }

    // Полный размер файла (для Content-Range) и длина отдаваемого куска (для Content-Length).
    public long TotalSize { get; set; }
    public long ContentLength { get; set; }

    // true → частичный ответ (206); RangeStart..RangeEnd включительно — для Content-Range.
    public bool IsPartial { get; set; }
    public long RangeStart { get; set; }
    public long RangeEnd { get; set; }
}
