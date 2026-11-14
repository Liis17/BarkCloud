using BarkCloud.Proto.Files;

using MediatR;

using DirectoryInfo = BarkCloud.Proto.Files.DirectoryInfo;

namespace BarkCloud.Files.Features.Cloud.SetDirectoryAppearance;

public class SetDirectoryAppearanceCommand : IRequest<DirectoryInfo>
{
    public Guid DirectoryId { get; set; }

    /// <summary>Ключ иконки из пака FolderIcons; пустая строка — сброс.</summary>
    public string IconKey { get; set; } = "";

    /// <summary>Цвет подложки #RRGGBB; пустая строка — сброс.</summary>
    public string IconColor { get; set; } = "";
}
