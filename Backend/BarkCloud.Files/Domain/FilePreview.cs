using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Связка между оригиналом-изображением и одним из его превью.
/// Превью хранится как отдельная запись <see cref="UploadFile"/> (со своим SHA256-хешем
/// и собственным набором Uploaders), а через эту таблицу мы умеем доставать список
/// превью по идентификатору оригинала.
/// </summary>
public class FilePreview
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Идентификатор оригинального UploadFile.
    /// </summary>
    public Guid OriginalFileId { get; set; }

    /// <summary>
    /// Идентификатор превью-UploadFile (отдельная запись с байтами в S3).
    /// </summary>
    public Guid PreviewFileId { get; set; }

    /// <summary>
    /// Запрошенная ширина превью (128 / 512 / 1024).
    /// </summary>
    public int TargetWidth { get; set; }

    /// <summary>
    /// Фактическая ширина после ресайза (может быть меньше TargetWidth,
    /// если оригинал был узким и ResizeMode.Max не увеличивает).
    /// </summary>
    public int ActualWidth { get; set; }

    /// <summary>
    /// Фактическая высота после ресайза.
    /// </summary>
    public int ActualHeight { get; set; }

    public DateTime CreatedAt { get; set; }
}
