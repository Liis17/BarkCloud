using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.CreateAlbumShare;

public class CreateAlbumShareCommand : IRequest<AlbumShareInfo>
{
    public Guid AlbumId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Срок действия; <c>null</c> — бессрочно.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Для уже опубликованного объекта — заменить срок ссылки на <see cref="ExpiresAt"/>.</summary>
    public bool ReplaceExpiry { get; set; }
}
