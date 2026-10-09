using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;

namespace BarkCloud.Files.Tests._Helpers;

internal static class CloudFileEntryFixtures
{
    // Фикстуры иерархии и грантов раньше создавали записи с произвольным FileId.
    // Теперь добавляем настоящие оригиналы, сохраняя включённый FK.
    public static void AddOriginals(FilesContext context, IEnumerable<CloudFileEntry> entries)
    {
        context.UploadedFiles.AddRange(entries.GroupBy(e => e.FileId).Select(group => new UploadFile
        {
            Id = group.Key,
            Uploaders = group.Select(e => e.OwnerId).Distinct().ToList(),
            StorageProfileId = "test-storage",
            Type = UploadFileType.CloudFile,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
            Etag = "etag",
        }));
    }
}
