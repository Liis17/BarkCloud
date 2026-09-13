namespace BarkCloud.Files.Domain;

/// <summary>
/// Последний подтверждённый Files ответ S3 для части multipart-сессии.
/// Хранится отдельно от сессии, чтобы не раздувать строку UploadSessions при больших файлах.
/// </summary>
public sealed class UploadSessionPart
{
    public Guid SessionId { get; set; }

    public int PartNumber { get; set; }

    public long Size { get; set; }

    public string Etag { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
