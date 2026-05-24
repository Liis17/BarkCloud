using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Album.UpdateAlbum;

public class UpdateAlbumCommand : IRequest<AlbumInfo>
{
    public Guid AlbumId { get; set; }

    /// <summary>null = не менять имя.</summary>
    public string? Name { get; set; }

    /// <summary>null = не менять описание.</summary>
    public string? Description { get; set; }

    /// <summary>true — менять обложку (значение в <see cref="CoverFileId"/>; null = сбросить).</summary>
    public bool UpdateCover { get; set; }

    public Guid? CoverFileId { get; set; }
}
