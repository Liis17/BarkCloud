using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

internal static class FileGrantQueries
{
    /// <summary>
    /// Оставляет гранты, чей файл не в корзине у выдавшего владельца: у него есть живая запись
    /// или нет записей в корзине (файл без привязки к папке). Семантика та же, что у
    /// <see cref="ICloudHierarchyStorage.GetEffectivelyTrashedFileIds"/>. Сам грант при удалении
    /// в корзину сохраняется — после восстановления доступ возвращается.
    /// </summary>
    public static IQueryable<FileGrant> WhereOwnerFileNotTrashed(this IQueryable<FileGrant> grants, FilesContext context)
    {
        return grants.Where(g =>
            context.CloudFileEntries.Any(e => e.OwnerId == g.OwnerId && e.FileId == g.FileId && !e.IsDeleted)
            || !context.CloudFileEntries.Any(e => e.OwnerId == g.OwnerId && e.FileId == g.FileId && e.IsDeleted));
    }
}
