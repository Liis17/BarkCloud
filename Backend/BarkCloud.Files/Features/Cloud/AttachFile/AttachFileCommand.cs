using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.AttachFile;

public class AttachFileCommand : IRequest<CloudEmpty>
{
    /// <summary>
    /// Идентификатор директории. null означает корень владельца.
    /// </summary>
    public Guid? DirectoryId { get; set; }

    public Guid FileId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>
    /// true → сервер игнорирует DirectoryId и кладёт файл в системную папку
    /// Фото/Видео/Другие документы по типу медиа (авто-распределение без явной папки).
    /// </summary>
    public bool RouteByMediaKind { get; set; }
}
