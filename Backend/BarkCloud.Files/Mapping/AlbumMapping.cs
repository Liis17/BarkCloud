using BarkCloud.Files.Domain;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

namespace BarkCloud.Files.Mapping;

public static class AlbumMapping
{
    /// <summary>
    /// Мапит альбом в gRPC-DTO. itemsCount и coverPreviewUrl вычисляются вызывающим кодом.
    /// </summary>
    public static AlbumInfo ToGrpc(this Album album, int itemsCount, string? coverPreviewUrl = null)
    {
        return new AlbumInfo
        {
            Id = album.Id.ToString(),
            Name = album.Name,
            Description = album.Description ?? string.Empty,
            CoverFileId = album.CoverFileId?.ToString() ?? string.Empty,
            CoverPreviewUrl = coverPreviewUrl ?? string.Empty,
            ItemsCount = itemsCount,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(album.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(album.UpdatedAt, DateTimeKind.Utc))
        };
    }
}
