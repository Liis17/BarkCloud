using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

public class FileHashesStorage : IFileHashesStorage
{
    private readonly FilesContext _context;

    public FileHashesStorage(FilesContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Adds a new file hash to the storage.
    /// </summary>
    public async Task AddHash(FileHash fileHash)
    {
        _context.FileHashes.Add(fileHash);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Gets the FileId associated with a given hash, or null if not found.
    /// </summary>
    public async Task<Guid?> GetFileIdByHash(string hash)
    {
        var fileHash = await _context.FileHashes
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Hash == hash);

        return fileHash?.FileId;
    }

    /// <summary>
    /// Возвращает все FileId с данным хешем. Дедупликация снята, поэтому одинаковый контент
    /// может относиться к нескольким блобам (индекс по Hash неуникальный).
    /// </summary>
    public async Task<List<Guid>> GetFileIdsByHash(string hash, CancellationToken cancellationToken = default)
    {
        return await _context.FileHashes
            .AsNoTracking()
            .Where(x => x.Hash == hash)
            .Select(x => x.FileId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Checks if a hash exists in the storage.
    /// </summary>
    public async Task<bool> HashExists(string hash)
    {
        return await _context.FileHashes
            .AsNoTracking()
            .AnyAsync(x => x.Hash == hash);
    }

    /// <summary>
    /// Пакетная проверка, ограниченная файлами ТЕКУЩЕГО пользователя: из набора хешей возвращает те,
    /// для которых у владельца есть живая (не в корзине) запись в облаке. Так дедуп-подсказка не
    /// раскрывает наличие файлов других пользователей. Ожидает нормализованные (lowercase) хеши.
    /// </summary>
    public async Task<HashSet<string>> GetExistingHashesForOwner(long ownerId, IReadOnlyCollection<string> hashes, CancellationToken cancellationToken = default)
    {
        if (hashes.Count == 0)
            return new HashSet<string>();

        var found = await _context.FileHashes
            .AsNoTracking()
            .Where(h => hashes.Contains(h.Hash)
                        && _context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == h.FileId && !e.IsDeleted))
            .Select(h => h.Hash)
            .ToListAsync(cancellationToken);

        return found.ToHashSet();
    }

    /// <summary>
    /// Gets the FileHash by FileId.
    /// </summary>
    public async Task<FileHash?> GetHashByFileId(Guid fileId)
    {
        return await _context.FileHashes
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.FileId == fileId);
    }

    /// <summary>
    /// Удаляет запись хеша по FileId. Идемпотентно: возвращает количество удалённых строк.
    /// Используется при дедупликации, чтобы не оставлять висячие хеши при удалении файла.
    /// </summary>
    public Task<int> DeleteHashByFileId(Guid fileId, CancellationToken cancellationToken = default)
    {
        return _context.FileHashes
            .Where(x => x.FileId == fileId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
