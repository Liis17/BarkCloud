using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

namespace BarkCloud.Files.Services;

/// <summary>
/// Собирает <see cref="DynamicFolderInfo"/> для умных папок: на каждую считает количество
/// подходящих файлов и берёт обложку (превью самого свежего файла). На папку приходится отдельный
/// COUNT + выборка первого файла — для немногих папок (системные + единицы пользовательских) это приемлемо.
/// </summary>
public class DynamicFolderViewBuilder
{
    private const int PreferredCoverWidth = 512;

    private readonly IDynamicFolderStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;

    public DynamicFolderViewBuilder(
        IDynamicFolderStorage storage,
        IUploadedFilesStorage filesStorage,
        RunSettings runSettings,
        IConfiguration configuration)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _runSettings = runSettings;
        _configuration = configuration;
    }

    public async Task<List<DynamicFolderInfo>> BuildAsync(long ownerId, IReadOnlyList<DynamicFolder> folders, CancellationToken cancellationToken)
    {
        var result = new List<DynamicFolderInfo>(folders.Count);
        if (folders.Count == 0)
            return result;

        var now = DateTime.UtcNow;
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        foreach (var folder in folders)
        {
            var count = await _storage.CountByCriteria(ownerId, folder.Criteria, now, cancellationToken);

            string? coverUrl = null;
            var first = await _storage.GetFirstItem(ownerId, folder.Criteria, now, cancellationToken);
            if (first is not null)
            {
                var previews = await _filesStorage.GetPreviewsForFile(first.Id, cancellationToken);
                if (previews.Count > 0)
                {
                    var chosen = previews.FirstOrDefault(p => p.TargetWidth == PreferredCoverWidth) ?? previews[^1];
                    coverUrl = FileUrlHelper.GenerateDownloadUrl(baseUrl, chosen.PreviewFileId);
                }
            }

            result.Add(folder.ToGrpc(count, coverUrl));
        }

        return result;
    }
}
