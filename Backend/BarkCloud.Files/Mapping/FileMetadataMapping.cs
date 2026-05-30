using BarkCloud.Files.Domain;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

namespace BarkCloud.Files.Mapping;

public static class FileMetadataMapping
{
    /// <summary>
    /// Domain → gRPC. Все поля nullable: пустые остаются «не выставленными» в protobuf,
    /// клиент видит их через <c>HasFoo</c> и показывает только заполненные.
    /// </summary>
    public static FileMetadataInfo ToGrpc(this FileMetadata m)
    {
        var info = new FileMetadataInfo();

        if (m.TakenAt.HasValue)
            info.TakenAt = Timestamp.FromDateTime(DateTime.SpecifyKind(m.TakenAt.Value, DateTimeKind.Utc));
        if (m.CreatorTool is not null) info.CreatorTool = m.CreatorTool;

        if (m.Latitude.HasValue) info.Latitude = m.Latitude.Value;
        if (m.Longitude.HasValue) info.Longitude = m.Longitude.Value;
        if (m.Altitude.HasValue) info.Altitude = m.Altitude.Value;

        if (m.CameraMake is not null) info.CameraMake = m.CameraMake;
        if (m.CameraModel is not null) info.CameraModel = m.CameraModel;
        if (m.LensModel is not null) info.LensModel = m.LensModel;

        if (m.FocalLengthMm.HasValue) info.FocalLengthMm = m.FocalLengthMm.Value;
        if (m.FNumber.HasValue) info.FNumber = m.FNumber.Value;
        if (m.ExposureTimeSeconds.HasValue) info.ExposureTimeSeconds = m.ExposureTimeSeconds.Value;
        if (m.Iso.HasValue) info.Iso = m.Iso.Value;
        if (m.Orientation.HasValue) info.Orientation = m.Orientation.Value;
        if (m.Flash.HasValue) info.Flash = m.Flash.Value;

        if (m.DurationSeconds.HasValue) info.DurationSeconds = m.DurationSeconds.Value;
        if (m.VideoCodec is not null) info.VideoCodec = m.VideoCodec;
        if (m.AudioCodec is not null) info.AudioCodec = m.AudioCodec;
        if (m.Bitrate.HasValue) info.Bitrate = m.Bitrate.Value;
        if (m.FrameRate.HasValue) info.FrameRate = m.FrameRate.Value;

        if (m.DocumentAuthor is not null) info.DocumentAuthor = m.DocumentAuthor;
        if (m.DocumentTitle is not null) info.DocumentTitle = m.DocumentTitle;
        if (m.DocumentSubject is not null) info.DocumentSubject = m.DocumentSubject;
        if (m.DocumentPageCount.HasValue) info.DocumentPageCount = m.DocumentPageCount.Value;

        return info;
    }
}
