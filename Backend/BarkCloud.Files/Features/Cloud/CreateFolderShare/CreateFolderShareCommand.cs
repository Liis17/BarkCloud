using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.CreateFolderShare;

public class CreateFolderShareCommand : IRequest<FolderShareInfo>
{
    public Guid DirectoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Срок действия; <c>null</c> — бессрочно.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Для уже опубликованного объекта — заменить срок ссылки на <see cref="ExpiresAt"/>.</summary>
    public bool ReplaceExpiry { get; set; }
}
