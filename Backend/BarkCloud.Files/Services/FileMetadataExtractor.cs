using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;

using DocumentFormat.OpenXml.Packaging;

using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

using UglyToad.PdfPig;

using Directory = MetadataExtractor.Directory;

namespace BarkCloud.Files.Services;

/// <summary>
/// Извлекает метаданные из блобов: EXIF/GPS из фото (этот файл),
/// QuickTime/ffprobe из видео (расширяется в <see cref="VideoThumbnailExtractor"/>),
/// CoreProperties из PDF/Office (будут добавлены отдельными методами).
/// Возвращает заполненный <see cref="FileMetadata"/> без <see cref="FileMetadata.FileId"/>
/// — идентификатор выставляет вызывающий после успешной загрузки в S3.
/// </summary>
public class FileMetadataExtractor
{
    private readonly ILogger<FileMetadataExtractor> _logger;

    public FileMetadataExtractor(ILogger<FileMetadataExtractor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Парсит EXIF/IPTC/XMP из изображения. Поддерживает JPEG/HEIC/PNG/TIFF и др. форматы,
    /// которые умеет читать MetadataExtractor. На вход — позиционируемый стрим.
    /// Возвращает null, если из контента не удалось извлечь ни одного поля
    /// (бессмысленно создавать пустую запись).
    /// </summary>
    public virtual FileMetadata? ExtractFromImage(Stream imageStream)
    {
        IReadOnlyList<Directory> directories;
        try
        {
            imageStream.Position = 0;
            directories = ImageMetadataReader.ReadMetadata(imageStream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetadataExtractor не смог прочитать изображение");
            return null;
        }

        var result = new FileMetadata();
        var hasAnything = false;

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();

        if (ifd0 is not null)
        {
            if (TryString(ifd0, ExifDirectoryBase.TagMake, out var make))
            { result.CameraMake = make; hasAnything = true; }

            if (TryString(ifd0, ExifDirectoryBase.TagModel, out var model))
            { result.CameraModel = model; hasAnything = true; }

            if (TryString(ifd0, ExifDirectoryBase.TagSoftware, out var software))
            { result.CreatorTool = software; hasAnything = true; }

            if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientation))
            { result.Orientation = orientation; hasAnything = true; }
        }

        if (subIfd is not null)
        {
            // Дата съёмки. EXIF не хранит часовой пояс (в старых тегах), поэтому
            // фиксируем как UTC-naive — Npgsql требует timestamptz с UTC-kind.
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var takenAt))
            {
                result.TakenAt = DateTime.SpecifyKind(takenAt, DateTimeKind.Utc);
                hasAnything = true;
            }

            if (TryString(subIfd, ExifDirectoryBase.TagLensModel, out var lens))
            { result.LensModel = lens; hasAnything = true; }

            if (subIfd.TryGetRational(ExifDirectoryBase.TagFocalLength, out var focal))
            { result.FocalLengthMm = focal.ToDouble(); hasAnything = true; }

            if (subIfd.TryGetRational(ExifDirectoryBase.TagFNumber, out var fnum))
            { result.FNumber = fnum.ToDouble(); hasAnything = true; }

            if (subIfd.TryGetRational(ExifDirectoryBase.TagExposureTime, out var exposure))
            { result.ExposureTimeSeconds = exposure.ToDouble(); hasAnything = true; }

            if (subIfd.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso))
            { result.Iso = iso; hasAnything = true; }

            if (subIfd.TryGetInt32(ExifDirectoryBase.TagFlash, out var flashRaw))
            {
                // Бит 0 в значении Flash — «вспышка сработала».
                result.Flash = (flashRaw & 0x1) == 0x1;
                hasAnything = true;
            }
        }

        if (gps is not null)
        {
            var location = gps.GetGeoLocation();
            if (location.HasValue && !location.Value.IsZero)
            {
                result.Latitude = location.Value.Latitude;
                result.Longitude = location.Value.Longitude;
                hasAnything = true;
            }

            if (gps.TryGetRational(GpsDirectory.TagAltitude, out var alt))
            {
                var altitude = alt.ToDouble();
                // GPSAltitudeRef = 1 → ниже уровня моря.
                if (gps.TryGetInt32(GpsDirectory.TagAltitudeRef, out var altRef) && altRef == 1)
                    altitude = -altitude;
                result.Altitude = altitude;
                hasAnything = true;
            }
        }

        return hasAnything ? result : null;
    }

    /// <summary>
    /// Метаданные видео: технические параметры (ProbeFullAsync) + теги контейнера
    /// (QuickTime/MP4): дата съёмки, GPS, устройство. Никогда не возвращает null —
    /// даже если тегов нет, всегда есть длительность/кодеки/fps.
    /// </summary>
    public virtual FileMetadata ExtractFromVideo(VideoProbe probe)
    {
        var result = new FileMetadata
        {
            DurationSeconds = probe.Duration.TotalSeconds > 0 ? probe.Duration.TotalSeconds : null,
            VideoCodec = string.IsNullOrWhiteSpace(probe.VideoCodec) ? null : probe.VideoCodec,
            AudioCodec = string.IsNullOrWhiteSpace(probe.AudioCodec) ? null : probe.AudioCodec,
            Bitrate = probe.BitRate > 0 ? probe.BitRate : null,
            FrameRate = probe.FrameRate > 0 ? probe.FrameRate : null,
            // Видео проходит ffprobe всегда — фиксируем признак HDR явно (true/false),
            // чтобы запись не считалась «не зондированной на цвет» (см. бэкафилл).
            IsHdr = VideoHdr.IsHdr(probe.ColorTransfer),
        };

        var tags = probe.FormatTags;
        if (tags is null)
            return result;

        // iPhone предпочитает com.apple.quicktime.creationdate (ISO-8601 с TZ),
        // иначе fallback на стандартный creation_time.
        var dateRaw = TryGet(tags, "com.apple.quicktime.creationdate")
                      ?? TryGet(tags, "creation_time");
        if (dateRaw is not null && DateTimeOffset.TryParse(dateRaw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            result.TakenAt = DateTime.SpecifyKind(dt.UtcDateTime, DateTimeKind.Utc);
        }

        // QuickTime ISO6709: «+59.9311+030.3609+010.000/» или «-12.34+056.78/».
        var iso6709 = TryGet(tags, "com.apple.quicktime.location.ISO6709")
                      ?? TryGet(tags, "location")
                      ?? TryGet(tags, "location-eng");
        if (iso6709 is not null && TryParseIso6709(iso6709, out var lat, out var lon, out var alt))
        {
            result.Latitude = lat;
            result.Longitude = lon;
            if (alt.HasValue)
                result.Altitude = alt.Value;
        }

        result.CameraMake ??= TryGet(tags, "com.apple.quicktime.make");
        result.CameraModel ??= TryGet(tags, "com.apple.quicktime.model");
        result.CreatorTool ??= TryGet(tags, "com.apple.quicktime.software")
                              ?? TryGet(tags, "encoder");

        return result;
    }

    private static string? TryGet(IReadOnlyDictionary<string, string> tags, string key)
        => tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>
    /// Парсит координаты в формате ISO 6709 («±DD.DDDD±DDD.DDDD±AAA.A/»).
    /// Высота опциональна — отдельный «±AAA.A» сегмент.
    /// </summary>
    internal static bool TryParseIso6709(string input, out double lat, out double lon, out double? alt)
    {
        lat = 0; lon = 0; alt = null;

        // Регекс: первый знаковый сегмент = lat, второй = lon, третий (опционально) = alt.
        var match = System.Text.RegularExpressions.Regex.Match(
            input,
            @"^([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)?/?$");
        if (!match.Success)
            return false;

        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lat))
            return false;
        if (!double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lon))
            return false;
        if (match.Groups[3].Success
            && double.TryParse(match.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a))
            alt = a;

        return true;
    }

    /// <summary>
    /// CoreProperties + число страниц из PDF (через PdfPig). Возвращает null,
    /// если ничего не извлеклось или документ нечитаем.
    /// </summary>
    public virtual FileMetadata? ExtractFromPdf(Stream pdfStream)
    {
        try
        {
            pdfStream.Position = 0;
            using var doc = PdfDocument.Open(pdfStream);
            var info = doc.Information;

            var result = new FileMetadata
            {
                DocumentAuthor = NullIfEmpty(info.Author),
                DocumentTitle = NullIfEmpty(info.Title),
                DocumentSubject = NullIfEmpty(info.Subject),
                CreatorTool = NullIfEmpty(info.Producer) ?? NullIfEmpty(info.Creator),
                DocumentPageCount = doc.NumberOfPages > 0 ? doc.NumberOfPages : null,
                TakenAt = ParsePdfDate(info.CreationDate),
            };

            return HasAnyField(result) ? result : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PdfPig не смог прочитать PDF-документ");
            return null;
        }
    }

    /// <summary>
    /// CoreProperties для DOCX/XLSX/PPTX (OpenXML SDK). Возвращает null
    /// для других форматов или если документ не открылся.
    /// </summary>
    public virtual FileMetadata? ExtractFromOffice(Stream officeStream, string contentType)
    {
        try
        {
            officeStream.Position = 0;
            DocumentFormat.OpenXml.Packaging.OpenXmlPackage package = contentType switch
            {
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                    => WordprocessingDocument.Open(officeStream, false),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                    => SpreadsheetDocument.Open(officeStream, false),
                "application/vnd.openxmlformats-officedocument.presentationml.presentation"
                    => PresentationDocument.Open(officeStream, false),
                _ => null!
            };

            if (package is null)
                return null;

            using (package)
            {
                var props = package.PackageProperties;
                var result = new FileMetadata
                {
                    DocumentAuthor = NullIfEmpty(props.Creator),
                    DocumentTitle = NullIfEmpty(props.Title),
                    DocumentSubject = NullIfEmpty(props.Subject),
                    TakenAt = props.Created.HasValue
                        ? DateTime.SpecifyKind(props.Created.Value.ToUniversalTime(), DateTimeKind.Utc)
                        : null,
                };

                // ExtendedFilePropertiesPart есть только у DOCX/PPTX и даёт счётчик страниц/слайдов.
                var extProps = (package as WordprocessingDocument)?.ExtendedFilePropertiesPart?.Properties
                              ?? (package as PresentationDocument)?.ExtendedFilePropertiesPart?.Properties;
                if (extProps is not null)
                {
                    result.CreatorTool ??= NullIfEmpty(extProps.Application?.Text);
                    if (int.TryParse(extProps.Pages?.Text, out var pages) && pages > 0)
                        result.DocumentPageCount = pages;
                    else if (int.TryParse(extProps.Slides?.Text, out var slides) && slides > 0)
                        result.DocumentPageCount = slides;
                }

                return HasAnyField(result) ? result : null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenXml не смог прочитать офисный документ ({ContentType})", contentType);
            return null;
        }
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// PDF Information.CreationDate — строка формата «D:YYYYMMDDHHmmSS+HH'mm'» или без префикса.
    /// </summary>
    private static DateTime? ParsePdfDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.StartsWith("D:") ? raw[2..] : raw;
        // YYYYMMDDHHmmSS — берём первые 14 цифр.
        if (s.Length < 14)
            return null;

        if (DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        return null;
    }

    private static bool HasAnyField(FileMetadata m)
        => m.TakenAt is not null || m.CreatorTool is not null
           || m.Latitude is not null || m.Longitude is not null || m.Altitude is not null
           || m.CameraMake is not null || m.CameraModel is not null || m.LensModel is not null
           || m.FocalLengthMm is not null || m.FNumber is not null || m.ExposureTimeSeconds is not null
           || m.Iso is not null || m.Orientation is not null || m.Flash is not null
           || m.DurationSeconds is not null || m.VideoCodec is not null || m.AudioCodec is not null
           || m.Bitrate is not null || m.FrameRate is not null
           || m.AudioTitle is not null || m.AudioArtist is not null || m.AudioAlbum is not null || m.AudioTrackNumber is not null
           || m.DocumentAuthor is not null || m.DocumentTitle is not null
           || m.DocumentSubject is not null || m.DocumentPageCount is not null;

    private static bool TryString(Directory dir, int tagType, out string value)
    {
        var raw = dir.GetString(tagType);
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = string.Empty;
            return false;
        }
        value = raw.Trim();
        return true;
    }
}
