using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Метаданные блоба: EXIF/XMP для фото, QuickTime/ffprobe для видео,
/// CoreProperties для PDF/Office. Привязка 1:1 к <see cref="UploadFile"/>
/// через <see cref="FileId"/>-PK — метаданные относятся к содержимому блоба,
/// а не к конкретному пользователю, поэтому дедупликация прозрачна.
/// Все поля nullable: заполняются только те, что удалось извлечь.
/// </summary>
public class FileMetadata
{
    /// <summary>
    /// Идентификатор связанного <see cref="UploadFile"/>. Одновременно PK и FK.
    /// </summary>
    [Key]
    public Guid FileId { get; set; }

    /// <summary>Дата создания записи в БД (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    // === Общие ===

    /// <summary>Дата/время съёмки или создания контента (EXIF DateTimeOriginal, QuickTime CreationDate, PDF /CreationDate).</summary>
    public DateTime? TakenAt { get; set; }

    /// <summary>Программа, которой был создан/отредактирован контент (EXIF Software, PDF /Producer, Office Application).</summary>
    public string? CreatorTool { get; set; }

    // === GPS ===

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>Высота над уровнем моря в метрах.</summary>
    public double? Altitude { get; set; }

    // === Камера ===

    /// <summary>Производитель устройства (EXIF Make / QuickTime com.apple.quicktime.make).</summary>
    public string? CameraMake { get; set; }

    /// <summary>Модель устройства (EXIF Model / QuickTime com.apple.quicktime.model).</summary>
    public string? CameraModel { get; set; }

    /// <summary>Модель объектива (EXIF LensModel).</summary>
    public string? LensModel { get; set; }

    // === Параметры съёмки ===

    /// <summary>Фокусное расстояние в миллиметрах.</summary>
    public double? FocalLengthMm { get; set; }

    /// <summary>Число диафрагмы (FNumber).</summary>
    public double? FNumber { get; set; }

    /// <summary>Выдержка в секундах (ExposureTime).</summary>
    public double? ExposureTimeSeconds { get; set; }

    /// <summary>Чувствительность ISO.</summary>
    public int? Iso { get; set; }

    /// <summary>EXIF-ориентация (1..8).</summary>
    public int? Orientation { get; set; }

    /// <summary>Использовалась ли вспышка.</summary>
    public bool? Flash { get; set; }

    // === Видео ===

    /// <summary>Длительность в секундах.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Кодек видео-потока (h264, hevc и т.п.).</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Кодек аудио-потока (aac, opus и т.п.).</summary>
    public string? AudioCodec { get; set; }

    /// <summary>Битрейт контейнера в битах в секунду.</summary>
    public long? Bitrate { get; set; }

    /// <summary>Частота кадров.</summary>
    public double? FrameRate { get; set; }

    // === Документ ===

    public string? DocumentAuthor { get; set; }
    public string? DocumentTitle { get; set; }
    public string? DocumentSubject { get; set; }
    public int? DocumentPageCount { get; set; }
}
