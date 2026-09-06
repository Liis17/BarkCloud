namespace BarkCloud.Files.Domain;

/// <summary>
/// Поле файла, по которому строится правило умной папки. Числовые значения
/// совпадают с proto-enum <c>DfField</c> (маппинг кастом <c>(int)</c>).
/// </summary>
public enum DfField
{
    None = 0,
    Date = 1,        // UploadFile.CreatedAt
    TakenAt = 2,     // FileMetadata.TakenAt
    Size = 3,        // UploadFile.Size (байты)
    Name = 4,        // UploadFile.Filename
    MediaKind = 5,   // UploadFile.MediaKind
    Extension = 6,   // расширение из Filename
    ImageWidth = 7,  // UploadFile.ImageWidth
    ImageHeight = 8, // UploadFile.ImageHeight
    Device = 9,         // UploadFile.UploadDeviceName (старое имя)
    MetadataDevice = 10 // FileMetadata.CameraMake + CameraModel
}
