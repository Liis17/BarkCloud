using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public static class DirectoryVisibilityQueries
{
    public static IQueryable<UploadFile> WhereNotHiddenFromGallery(
        this IQueryable<UploadFile> files, FilesContext context, long ownerId) =>
        files.Where(file => !context.CloudFileEntries.Any(entry =>
            entry.OwnerId == ownerId && entry.FileId == file.Id && !entry.IsDeleted
            && context.CloudDirectories.Any(directory =>
                directory.OwnerId == ownerId && directory.Id == entry.DirectoryId && directory.EffectiveHideFromGallery)));

    public static IQueryable<UploadFile> WhereNotHiddenFromSearch(
        this IQueryable<UploadFile> files, FilesContext context, long ownerId) =>
        files.Where(file => !context.CloudFileEntries.Any(entry =>
            entry.OwnerId == ownerId && entry.FileId == file.Id && !entry.IsDeleted
            && context.CloudDirectories.Any(directory =>
                directory.OwnerId == ownerId && directory.Id == entry.DirectoryId && directory.EffectiveHideFromSearch)));
}
