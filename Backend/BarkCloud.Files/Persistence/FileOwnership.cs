using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Атомарные изменения списка владельцев <see cref="Domain.UploadFile.Uploaders"/>. Прочитать список,
/// изменить в памяти и сохранить целиком нельзя: два параллельных запроса из <c>[A]</c> получат
/// <c>[A,B]</c> и <c>[A,C]</c>, и последний сохранивший затрёт чужого владельца. Здесь каждое изменение —
/// один SQL-оператор над массивом (PostgreSQL перепроверяет условие после ожидания блокировки строки),
/// а решения «на блоб ещё кто-то ссылается» принимаются под блокировкой строки блоба (<see cref="LockLiveFilesAsync"/>).
/// На других провайдерах (SQLite в юнит-тестах) блокировок нет — используется прежнее «прочитать-изменить-сохранить».
/// </summary>
internal static class FileOwnership
{
    /// <summary>Добавляет владельца, если его ещё нет. Неизвестный файл — не ошибка.</summary>
    public static async Task AddUploaderAsync(
        this FilesContext context, Guid fileId, long userId, CancellationToken cancellationToken = default)
    {
        if (context.IsNpgsql())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "UploadedFiles"
                 SET "Uploaders" = array_append("Uploaders", {userId})
                 WHERE "Id" = {fileId} AND NOT ("Uploaders" @> ARRAY[{userId}]::bigint[])
                 """,
                cancellationToken);
            return;
        }

        var file = await context.UploadedFiles.FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);
        if (file is null || file.Uploaders.Contains(userId))
            return;

        file.Uploaders.Add(userId);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Снимает владельца с файла. Неизвестный файл или отсутствующий владелец — не ошибка.</summary>
    public static async Task RemoveUploaderAsync(
        this FilesContext context, Guid fileId, long userId, CancellationToken cancellationToken = default)
    {
        if (context.IsNpgsql())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "UploadedFiles"
                 SET "Uploaders" = array_remove("Uploaders", {userId})
                 WHERE "Id" = {fileId} AND "Uploaders" @> ARRAY[{userId}]::bigint[]
                 """,
                cancellationToken);
            return;
        }

        var file = await context.UploadedFiles.FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);
        if (file is not null && file.Uploaders.Remove(userId))
            await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Снимает пользователя со всех файлов одним оператором. Возвращает число затронутых файлов.</summary>
    public static async Task<int> RemoveUploaderFromAllAsync(
        this FilesContext context, long userId, CancellationToken cancellationToken = default)
    {
        if (context.IsNpgsql())
        {
            return await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "UploadedFiles"
                 SET "Uploaders" = array_remove("Uploaders", {userId})
                 WHERE "Uploaders" @> ARRAY[{userId}]::bigint[]
                 """,
                cancellationToken);
        }

        var files = await context.UploadedFiles
            .Where(x => x.Uploaders.Contains(userId))
            .ToListAsync(cancellationToken);
        foreach (var file in files)
            file.Uploaders.Remove(userId);
        if (files.Count > 0)
            await context.SaveChangesAsync(cancellationToken);
        return files.Count;
    }

    /// <summary>
    /// Блокирует строки «живых» файлов (есть хотя бы один владелец) до конца текущей транзакции и возвращает
    /// их идентификаторы. Несуществующие и осиротевшие (пустой <c>Uploaders</c>) файлы не возвращаются:
    /// на них ссылаться уже нельзя — их удаляет очистка. Блокировка идёт по возрастанию <c>Id</c>, чтобы
    /// две транзакции с пересекающимися наборами не ждали друг друга. Вызывать только внутри транзакции.
    /// </summary>
    public static async Task<List<Guid>> LockLiveFilesAsync(
        this FilesContext context, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        var ids = fileIds.Distinct().ToArray();
        if (ids.Length == 0)
            return [];

        if (!context.IsNpgsql())
        {
            return await context.UploadedFiles
                .AsNoTracking()
                .Where(x => ids.Contains(x.Id) && x.Uploaders.Count > 0)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }

        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Блокировка строк файлов возможна только внутри транзакции.");

        return await context.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT "Id" AS "Value"
                 FROM "UploadedFiles"
                 WHERE "Id" = ANY({ids}) AND cardinality("Uploaders") > 0
                 ORDER BY "Id"
                 FOR UPDATE
                 """)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Снимает <paramref name="ownerId"/> с превью-блобов, которые ему больше не нужны. Превью дедуплицируются
    /// по SHA256 и могут быть общими для нескольких оригиналов, поэтому владелец снимается, только если не
    /// осталось другого оригинала (кроме <paramref name="releasedOriginalIds"/>), который его использует.
    /// Проверка и снятие идут под блокировкой строки превью — параллельное освобождение/привязка ждёт
    /// и видит уже закоммиченный результат. Вызывать внутри транзакции.
    /// </summary>
    public static async Task ReleasePreviewOwnerAsync(
        this FilesContext context,
        IReadOnlyCollection<Guid> previewFileIds,
        long ownerId,
        IReadOnlyCollection<Guid> releasedOriginalIds,
        CancellationToken cancellationToken = default)
    {
        foreach (var previewFileId in await context.LockLiveFilesAsync(previewFileIds, cancellationToken))
        {
            var stillNeeded = await context.FilePreviews
                .AsNoTracking()
                .AnyAsync(p => p.PreviewFileId == previewFileId
                    && !releasedOriginalIds.Contains(p.OriginalFileId)
                    && context.UploadedFiles.Any(o => o.Id == p.OriginalFileId && o.Uploaders.Contains(ownerId)),
                    cancellationToken);

            if (!stillNeeded)
                await context.RemoveUploaderAsync(previewFileId, ownerId, cancellationToken);
        }
    }

    private static bool IsNpgsql(this FilesContext context) =>
        context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
}
