using System.Linq.Expressions;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Proto.Files;

using Microsoft.EntityFrameworkCore;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;
using DomainUploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Services;

/// <summary>
/// Источники секций поиска. Каждый источник сам считает ранг и порядок в БД и отдаёт только
/// <see cref="SectionQuery.Take"/> строк после курсора, поэтому объём чтения не зависит от числа совпадений.
/// Тот же код разрешает один хит (<see cref="SectionQuery.ResolveId"/>): без запроса ранг нулевой,
/// а выборка ограничена идентификатором и проверкой доступа.
/// </summary>
public partial class UnifiedSearchService
{
    /// <summary>Что загрузить: страница поиска (<see cref="Terms"/> + курсор) или один хит по id.</summary>
    private sealed record SectionQuery(SearchTerms Terms, SearchCursor? Cursor, int Take, string? ResolveId, SearchHitKind? ResolveKind)
    {
        public bool IsResolve => ResolveId is not null;

        public Guid? ResolveGuid => Guid.TryParse(ResolveId, out var id) ? id : null;

        /// <summary>Разрешение по id, который не может быть Guid: БД-источникам искать нечего.</summary>
        public bool NothingToFind => IsResolve && ResolveGuid is null;

        public bool Wants(SearchHitKind kind) => ResolveKind is null || ResolveKind == kind;
    }

    private sealed class FileRow
    {
        public UploadFile File { get; set; } = null!;

        public CloudFileEntry? Entry { get; set; }
    }

    private sealed class MetaRow
    {
        public FileMetadata Meta { get; set; } = null!;

        public DomainMediaKind Kind { get; set; }
    }

    // Классы с инициализаторами (не позиционные записи): EF должен видеть свойства строки в последующих условиях.
    private sealed class SharedFileRow
    {
        public FileGrant Grant { get; set; } = null!;

        public UploadFile File { get; set; } = null!;
    }

    private sealed class SharedFolderRow
    {
        public DirectoryGrant Grant { get; set; } = null!;

        public CloudDirectory Directory { get; set; } = null!;
    }

    private sealed class SharedPlaylistRow
    {
        public MusicPlaylistGrant Grant { get; set; } = null!;

        public MusicPlaylist Playlist { get; set; } = null!;
    }

    private Task<List<PendingHit>> LoadSection(SearchSection section, SectionQuery query, CancellationToken cancellationToken) => section switch
    {
        SearchSection.Photos or SearchSection.Videos or SearchSection.Files or SearchSection.Tracks or SearchSection.Trash
            => LoadOwnedFiles(section, query, cancellationToken),
        SearchSection.Albums => LoadAlbums(query, cancellationToken),
        SearchSection.Playlists => LoadPlaylists(query, cancellationToken),
        SearchSection.Folders => LoadFolders(query, cancellationToken),
        SearchSection.Shared => LoadShared(query, cancellationToken),
        _ => Task.FromResult(new List<PendingHit>())
    };

    private async Task<List<PendingHit>> LoadOwnedFiles(SearchSection section, SectionQuery query, CancellationToken cancellationToken)
    {
        if (query.NothingToFind)
            return [];

        var ownerId = _userContext.UserId;
        var terms = query.Terms;
        var trash = section == SearchSection.Trash;
        var hitKind = section switch
        {
            SearchSection.Photos => SearchHitKind.Photo,
            SearchSection.Videos => SearchHitKind.Video,
            SearchSection.Tracks => SearchHitKind.Track,
            SearchSection.Trash => SearchHitKind.Trash,
            _ => SearchHitKind.File
        };

        var files = OwnedFiles(ownerId, section);
        if (query.ResolveGuid is { } resolveId)
        {
            // Хит открывают по id записи облака или по id файла: сначала точечно находим файл, чтобы БД не просматривала каталог.
            var candidates = await _context.CloudFileEntries.AsNoTracking()
                .Where(e => e.OwnerId == ownerId && e.Id == resolveId)
                .Select(e => e.FileId)
                .ToListAsync(cancellationToken);
            candidates.Add(resolveId);
            files = files.Where(f => candidates.Contains(f.Id));
        }

        // Запись, под именем которой файл показан: живая, а в корзине — последняя из удалённых (если живой нет).
        var live = _context.CloudFileEntries.AsNoTracking().Where(e => e.OwnerId == ownerId && !e.IsDeleted);
        var deleted = _context.CloudFileEntries.AsNoTracking().Where(e => e.OwnerId == ownerId && e.IsDeleted);
        var titles = trash
            ? deleted.Where(d => !live.Any(l => l.FileId == d.FileId)
                                 && !deleted.Any(o => o.FileId == d.FileId
                                                      && (o.DeletedAt > d.DeletedAt || o.DeletedAt == d.DeletedAt && o.Id.CompareTo(d.Id) > 0)))
            : live;

        var ranked = terms.HasQuery
            ? from f in files
              join b in BestFileMatches(ownerId, terms, files, titles, section) on f.Id equals b.Item
              select new Ranked<UploadFile> { Item = f, Rank = b.Rank, Sim = b.Sim }
            : files.Select(f => new Ranked<UploadFile> { Item = f, Rank = 0, Sim = 0d });

        var rows = trash
            ? from r in ranked
              join e in titles on r.Item.Id equals e.FileId
              select new SearchRow<FileRow>
              {
                  Item = new FileRow { File = r.Item, Entry = e },
                  Rank = r.Rank,
                  Sim = r.Sim,
                  SortAt = e.DeletedAt ?? e.CreatedAt,
                  Id = e.Id
              }
            : from r in ranked
              join e in titles on r.Item.Id equals e.FileId into entries
              from e in entries.DefaultIfEmpty()
              // Файл без живой записи, но с удалённой — в корзине, в обычные разделы не попадает.
              where e != null || !deleted.Any(d => d.FileId == r.Item.Id)
              select new SearchRow<FileRow>
              {
                  Item = new FileRow { File = r.Item, Entry = e },
                  Rank = r.Rank,
                  Sim = r.Sim,
                  SortAt = r.Item.CreatedAt,
                  Id = e != null ? e.Id : r.Item.Id
              };
        if (query.ResolveGuid is { } id)
            rows = rows.Where(r => r.Id == id || r.Item.File.Id == id);

        var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
        return page.Select(r => new PendingHit(hitKind, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.File.Id,
            data => OwnedFileHit(r.Item.File, r.Item.Entry, trash, hitKind, terms.Query, data))).ToList();
    }

    /// <summary>Готовые, принадлежащие владельцу файлы облака (без превью-блобов) нужного раздела.</summary>
    private IQueryable<UploadFile> OwnedFiles(long ownerId, SearchSection section)
    {
        var files = _context.UploadedFiles.AsNoTracking()
            .WhereReady()
            .Where(x => x.Uploaders.Contains(ownerId)
                        && x.Type == DomainUploadFileType.CloudFile
                        && !_context.FilePreviews.Any(p => p.PreviewFileId == x.Id));
        return section switch
        {
            SearchSection.Photos => files.Where(x => x.MediaKind == DomainMediaKind.Photo),
            SearchSection.Videos => files.Where(x => x.MediaKind == DomainMediaKind.Video),
            SearchSection.Tracks => files.Where(x => x.MediaKind == DomainMediaKind.Audio),
            SearchSection.Files => files.Where(x => x.MediaKind != DomainMediaKind.Photo
                                                    && x.MediaKind != DomainMediaKind.Video
                                                    && x.MediaKind != DomainMediaKind.Audio),
            _ => files
        };
    }

    /// <summary>
    /// Лучший ранг файла по всем полям, по которым он находится: имя записи, алиас и теги владельца,
    /// имя блоба у файла без записей, аудио- и документные метаданные. Каждое поле ищется своим индексом,
    /// затем строки сворачиваются по файлу; считаются только совпавшие строки.
    /// </summary>
    private IQueryable<Ranked<Guid>> BestFileMatches(
        long ownerId, SearchTerms terms, IQueryable<UploadFile> files, IQueryable<CloudFileEntry> titles, SearchSection section)
    {
        // Источники ограничены файлами секции: ранг не считается для чужих разделов (в пустой секции работы почти нет).
        var hits = titles.Where(e => files.Any(f => f.Id == e.FileId))
                .RankedBy(terms, e => e.FileId, true, e => e.Name)
            .Concat(_context.FileSearchAliases.AsNoTracking().Where(a => a.OwnerId == ownerId && files.Any(f => f.Id == a.FileId))
                .RankedBy(terms, a => a.FileId, true, a => a.NormalizedValue))
            .Concat(_context.FileTags.AsNoTracking().Where(t => t.OwnerId == ownerId && files.Any(f => f.Id == t.FileId))
                .RankedBy(terms, t => t.FileId, true, t => t.NormalizedValue));
        if (section != SearchSection.Trash)
            hits = hits.Concat(files.Where(f => !_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id))
                .RankedBy(terms, f => f.Id, true, f => f.Filename));

        var audio = section is SearchSection.Tracks or SearchSection.Trash;
        var document = section is SearchSection.Files or SearchSection.Trash;
        if (audio || document)
        {
            var raw = new List<Expression<Func<FileMetadata, string?>>>();
            var fields = new List<Expression<Func<MetaRow, string?>>>();
            if (audio)
            {
                raw.AddRange([m => m.AudioTitle, m => m.AudioArtist, m => m.AudioAlbum]);
                fields.AddRange([
                    r => r.Kind == DomainMediaKind.Audio ? r.Meta.AudioTitle : null,
                    r => r.Kind == DomainMediaKind.Audio ? r.Meta.AudioArtist : null,
                    r => r.Kind == DomainMediaKind.Audio ? r.Meta.AudioAlbum : null]);
            }
            if (document)
            {
                raw.AddRange([m => m.DocumentTitle, m => m.DocumentAuthor, m => m.DocumentSubject]);
                fields.AddRange([
                    r => r.Kind == DomainMediaKind.Document ? r.Meta.DocumentTitle : null,
                    r => r.Kind == DomainMediaKind.Document ? r.Meta.DocumentAuthor : null,
                    r => r.Kind == DomainMediaKind.Document ? r.Meta.DocumentSubject : null]);
            }

            // Условие по колонкам идёт до соединения (trigram-индексы), вид файла учитывается при расчёте ранга.
            var metaRows = from m in _context.FileMetadata.AsNoTracking().WhereMatchesAny(terms, raw.ToArray())
                           join f in files on m.FileId equals f.Id
                           select new MetaRow { Meta = m, Kind = f.MediaKind };
            hits = hits.Concat(metaRows.RankedBy(terms, r => r.Meta.FileId, false, fields.ToArray()).Where(h => h.Rank > 0));
        }

        return hits.GroupBy(h => h.Item)
            .Select(g => new Ranked<Guid> { Item = g.Key, Rank = g.Max(h => h.Rank), Sim = g.Max(h => h.Sim) });
    }

    private async Task<List<PendingHit>> LoadAlbums(SectionQuery query, CancellationToken cancellationToken)
    {
        if (query.NothingToFind)
            return [];

        var ownerId = _userContext.UserId;
        var rows = _context.Albums.AsNoTracking().Where(x => x.OwnerId == ownerId)
            .RankedBy(query.Terms, a => a, true, a => a.Name, a => a.Description)
            .Select(r => new SearchRow<Album> { Item = r.Item, Rank = r.Rank, Sim = r.Sim, SortAt = r.Item.UpdatedAt, Id = r.Item.Id });
        if (query.ResolveGuid is { } id)
            rows = rows.Where(r => r.Id == id);

        var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
        return page.Select(r => SimpleHit(SearchHitKind.Album, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Name, r.Item.Description, null,
            query.Terms.Query, ("name", r.Item.Name), ("description", r.Item.Description))).ToList();
    }

    private async Task<List<PendingHit>> LoadPlaylists(SectionQuery query, CancellationToken cancellationToken)
    {
        if (query.NothingToFind)
            return [];

        var ownerId = _userContext.UserId;
        var rows = _context.MusicPlaylists.AsNoTracking().Where(x => x.OwnerId == ownerId)
            .RankedBy(query.Terms, p => p, true, p => p.Name, p => p.Description)
            .Select(r => new SearchRow<MusicPlaylist> { Item = r.Item, Rank = r.Rank, Sim = r.Sim, SortAt = r.Item.UpdatedAt, Id = r.Item.Id });
        if (query.ResolveGuid is { } id)
            rows = rows.Where(r => r.Id == id);

        var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
        return page.Select(r => SimpleHit(SearchHitKind.Playlist, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Name, r.Item.Description, null,
            query.Terms.Query, ("name", r.Item.Name), ("description", r.Item.Description))).ToList();
    }

    /// <summary>Папки: каталоги и пользовательские умные папки из БД плюс системные умные папки из кода (их 6, ранжируются в памяти).</summary>
    private async Task<List<PendingHit>> LoadFolders(SectionQuery query, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var result = new List<PendingHit>();

        if (query.Wants(SearchHitKind.Folder) && !query.NothingToFind)
        {
            var rows = _context.CloudDirectories.AsNoTracking().Where(x => x.OwnerId == ownerId)
                .RankedBy(query.Terms, d => d, true, d => d.Name)
                .Select(r => new SearchRow<CloudDirectory> { Item = r.Item, Rank = r.Rank, Sim = r.Sim, SortAt = r.Item.UpdatedAt, Id = r.Item.Id });
            if (query.ResolveGuid is { } id)
                rows = rows.Where(r => r.Id == id);

            var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
            result.AddRange(page.Select(r => SimpleHit(SearchHitKind.Folder, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Name, "Папка", null,
                query.Terms.Query, ("name", r.Item.Name))));
        }

        if (query.Wants(SearchHitKind.DynamicFolder))
        {
            if (!query.NothingToFind)
            {
                var rows = _context.DynamicFolders.AsNoTracking().Where(x => x.OwnerId == ownerId)
                    .RankedBy(query.Terms, f => f, true, f => f.Name)
                    .Select(r => new SearchRow<DynamicFolder> { Item = r.Item, Rank = r.Rank, Sim = r.Sim, SortAt = r.Item.UpdatedAt, Id = r.Item.Id });
                if (query.ResolveGuid is { } id)
                    rows = rows.Where(r => r.Id == id);

                var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
                result.AddRange(page.Select(r => SimpleHit(SearchHitKind.DynamicFolder, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Name, "Умная папка", null,
                    query.Terms.Query, ("name", r.Item.Name))));
            }

            foreach (var folder in SystemDynamicFolders.All())
            {
                var key = folder.SystemKey ?? string.Empty;
                if (query.IsResolve && key != query.ResolveId)
                    continue;
                var match = Match(query.Terms.Query, [("name", folder.Name)]);
                if (query.Terms.HasQuery && match is null)
                    continue;
                var hit = SimpleHit(SearchHitKind.DynamicFolder, key, match?.Rank ?? 0, match?.Similarity ?? 0, folder.UpdatedAt, folder.Name,
                    "Системная умная папка", null, query.Terms.Query, ("name", folder.Name));
                if (query.Cursor is null || IsAfter(hit, query.Cursor))
                    result.Add(hit);
            }
        }

        return Top(result, query.Take);
    }

    private async Task<List<PendingHit>> LoadShared(SectionQuery query, CancellationToken cancellationToken)
    {
        if (query.NothingToFind)
            return [];

        var recipientId = _userContext.UserId;
        var terms = query.Terms;
        var result = new List<PendingHit>();

        if (query.Wants(SearchHitKind.SharedFile))
        {
            var rows = (from grant in _context.FileGrants.AsNoTracking().WhereOwnerFileNotTrashed(_context)
                        join file in _context.UploadedFiles.AsNoTracking().WhereReady() on grant.FileId equals file.Id
                        where grant.RecipientId == recipientId
                        select new SharedFileRow { Grant = grant, File = file })
                .RankedBy(terms, r => r, true, r => r.File.Filename)
                .Select(r => new SearchRow<SharedFileRow> { Item = r.Item, Rank = r.Rank, Sim = r.Sim, SortAt = r.Item.Grant.CreatedAt, Id = r.Item.Grant.Id });
            if (query.ResolveGuid is { } id)
                rows = rows.Where(r => r.Id == id || r.Item.File.Id == id);

            var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
            result.AddRange(page.Select(r => SimpleHit(SearchHitKind.SharedFile, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.File.Filename ?? "Файл",
                "Доступный файл", r.Item.File.Id, terms.Query, ("name", r.Item.File.Filename))));
        }

        if (query.Wants(SearchHitKind.SharedFolder))
        {
            var rows = (from grant in _context.DirectoryGrants.AsNoTracking()
                        join dir in _context.CloudDirectories.AsNoTracking() on grant.DirectoryId equals dir.Id
                        where grant.RecipientId == recipientId
                        select new SharedFolderRow { Grant = grant, Directory = dir })
                .RankedBy(terms, r => r, true, r => r.Directory.Name)
                .Select(r => new SearchRow<SharedFolderRow> { Item = r.Item, Rank = r.Rank, Sim = r.Sim, SortAt = r.Item.Grant.CreatedAt, Id = r.Item.Grant.Id });
            if (query.ResolveGuid is { } id)
                rows = rows.Where(r => r.Id == id || r.Item.Directory.Id == id);

            var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
            result.AddRange(page.Select(r => SimpleHit(SearchHitKind.SharedFolder, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Directory.Name,
                "Доступная папка", r.Item.Directory.Id, terms.Query, ("name", r.Item.Directory.Name))));
        }

        if (query.Wants(SearchHitKind.SharedPlaylist))
        {
            var rows = (from grant in _context.MusicPlaylistGrants.AsNoTracking()
                        join playlist in _context.MusicPlaylists.AsNoTracking() on grant.PlaylistId equals playlist.Id
                        where grant.RecipientId == recipientId
                        select new SharedPlaylistRow { Grant = grant, Playlist = playlist })
                .RankedBy(terms, r => r, true, r => r.Playlist.Name)
                .Select(r => new SearchRow<SharedPlaylistRow> { Item = r.Item, Rank = r.Rank, Sim = r.Sim, SortAt = r.Item.Grant.CreatedAt, Id = r.Item.Grant.Id });
            if (query.ResolveGuid is { } id)
                rows = rows.Where(r => r.Id == id || r.Item.Playlist.Id == id);

            var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
            result.AddRange(page.Select(r => SimpleHit(SearchHitKind.SharedPlaylist, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Playlist.Name,
                "Доступный плейлист", r.Item.Playlist.Id, terms.Query, ("name", r.Item.Playlist.Name))));
        }

        return Top(result, query.Take);
    }

    /// <summary>Слияние страниц нескольких источников: top-N каждого источника достаточно для общего top-N.</summary>
    private static List<PendingHit> Top(List<PendingHit> hits, int take)
        => hits.OrderByDescending(x => x.Rank)
            .ThenByDescending(x => x.Similarity)
            .ThenByDescending(x => x.SortAt)
            .ThenByDescending(x => x.Id, StringComparer.Ordinal)
            .Take(take)
            .ToList();

    /// <summary>Хит идёт после курсора в порядке выдачи (для источников в памяти).</summary>
    private static bool IsAfter(PendingHit hit, SearchCursor cursor)
    {
        if (hit.Rank != cursor.Rank)
            return hit.Rank < cursor.Rank;
        if (hit.Similarity != cursor.Similarity)
            return hit.Similarity < cursor.Similarity;
        if (hit.SortAt.Ticks != cursor.Ticks)
            return hit.SortAt.Ticks < cursor.Ticks;
        return string.CompareOrdinal(hit.Id, cursor.Id) < 0;
    }
}
