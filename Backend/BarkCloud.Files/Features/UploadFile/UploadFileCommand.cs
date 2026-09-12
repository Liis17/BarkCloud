using MediatR;

namespace BarkCloud.Files.Features.UploadFile;

public class UploadFileCommand : IRequest<string>, IDisposable
{
    public Guid FileId { get; set; }

    public Stream FileStream { get; set; }

    public string FileName { get; set; }

    public string? ContentTypeOverride { get; set; }

    public long FileSize { get; set; }

    public bool OriginalAlreadyStored { get; set; }

    public string? StoredEtag { get; set; }

    public string? ExpectedSha256 { get; set; }

    public string? StorageProfileIdOverride { get; set; }

    public bool ForceDiskBuffer { get; set; }

    public bool DeferAvailability { get; set; }

    public Guid? QuotaReservationId { get; set; }

    public void Dispose()
    {
        FileStream?.Dispose();
    }
}
