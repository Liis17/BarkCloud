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
            var resolvedFileIds = await _context.CloudFileEntries.AsNoTracking()
                .Where(e => e.OwnerId == ownerId && e.Id == resolveId)
                .Select(e => e.FileId)
                .ToListAsync(cancellationToken);
            resolvedFileIds.Add(resolveId);
            files = files.Where(f => resolvedFileIds.Contains(f.Id));
        }

        // Запись, под именем которой файл показан: живая, а в корзине — последняя из удалённых (если живой нет).
        var live = _context.CloudFileEntries.AsNoTracking().Where(e => e.OwnerId == ownerId && !e.IsDeleted);
        var deleted = _context.CloudFileEntries.AsNoTracking().Where(e => e.OwnerId == ownerId && e.IsDeleted);
        var titles = trash
            ? deleted.Where(d => !live.Any(l => l.FileId == d.FileId)
                                 && !deleted.Any(o => o.FileId == d.FileId
                                                      && (o.DeletedAt > d.DeletedAt || o.DeletedAt == d.DeletedAt && o.Id.CompareTo(d.Id) > 0)))
            : live;

        var candidates = terms.HasQuery ? FileCandidates(ownerId, terms, files, titles, section) : null;
        var ranked = candidates is not null
            ? from f in files
              join b in candidates.BestMatches() on f.Id equals b.Item
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

        var pageQuery = rows.After(query.Cursor).InSearchOrder().Take(query.Take);
        if (candidates is not null)
            pageQuery = pageQuery.InSearchOrder().WithMatchCandidates(candidates, r => r.Item.File.Id);
        var page = await pageQuery.ToListAsync(cancellationToken);
        return page.Select(r => new PendingHit(hitKind, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.File.Id,
            r.MatchField, r.MatchValue, data => OwnedFileHit(r.Item.File, r.Item.Entry, trash, hitKind, data))).ToList();
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
    /// Кандидаты файла по именам, метаданным, алиасу и тегам владельца. Каждая ветка — отдельное поле
    /// с собственным условием поиска и тем же SQL-рангом, который выбирает подпись.
    /// </summary>
    private IQueryable<SearchCandidate<Guid>> FileCandidates(
        long ownerId, SearchTerms terms, IQueryable<UploadFile> files, IQueryable<CloudFileEntry> titles, SearchSection section)
    {
        // Источники ограничены файлами секции: ранг не считается для чужих разделов (в пустой секции работы почти нет).
        var hits = titles.Where(e => files.Any(f => f.Id == e.FileId))
                .CandidatesBy(terms, e => e.FileId, "name", 0, e => e.Name)
            .Concat(_context.FileSearchAliases.AsNoTracking().Where(a => a.OwnerId == ownerId && files.Any(f => f.Id == a.FileId))
                .CandidatesBy(terms, a => a.FileId, "alias", 1, a => a.NormalizedValue, a => a.Value))
            .Concat(_context.FileTags.AsNoTracking().Where(t => t.OwnerId == ownerId && files.Any(f => f.Id == t.FileId))
                .CandidatesBy(terms, t => t.FileId, "tag", 2, t => t.NormalizedValue, t => t.Value));
        if (section != SearchSection.Trash)
            hits = hits.Concat(files.Where(f => !_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id))
                .CandidatesBy(terms, f => f.Id, "name", 0, f => f.Filename));

        var audio = section is SearchSection.Tracks or SearchSection.Trash;
        var document = section is SearchSection.Files or SearchSection.Trash;
        var metadata = _context.FileMetadata.AsNoTracking();
        if (audio)
        {
            var audioFiles = files.Where(f => f.MediaKind == DomainMediaKind.Audio);
            var audioMetadata = metadata.Where(m => audioFiles.Any(f => f.Id == m.FileId));
            hits = hits.Concat(audioMetadata.CandidatesBy(terms, m => m.FileId, "title", 3, m => m.AudioTitle));
            hits = hits.Concat(audioMetadata.CandidatesBy(terms, m => m.FileId, "artist", 4, m => m.AudioArtist));
            hits = hits.Concat(audioMetadata.CandidatesBy(terms, m => m.FileId, "album", 5, m => m.AudioAlbum));
        }
        if (document)
        {
            var documentFiles = files.Where(f => f.MediaKind == DomainMediaKind.Document);
            var documentMetadata = metadata.Where(m => documentFiles.Any(f => f.Id == m.FileId));
            hits = hits.Concat(documentMetadata.CandidatesBy(terms, m => m.FileId, "documentTitle", 6, m => m.DocumentTitle));
            hits = hits.Concat(documentMetadata.CandidatesBy(terms, m => m.FileId, "documentAuthor", 7, m => m.DocumentAuthor));
            hits = hits.Concat(documentMetadata.CandidatesBy(terms, m => m.FileId, "documentSubject", 8, m => m.DocumentSubject));
        }

        return hits;
    }

    private async Task<List<PendingHit>> LoadAlbums(SectionQuery query, CancellationToken cancellationToken)
    {
        if (query.NothingToFind)
            return [];

        var ownerId = _userContext.UserId;
        var albums = _context.Albums.AsNoTracking().Where(x => x.OwnerId == ownerId);
        var candidates = query.Terms.HasQuery
            ? albums.CandidatesBy(query.Terms, a => a.Id, "name", 0, a => a.Name)
                .Concat(albums.CandidatesBy(query.Terms, a => a.Id, "description", 1, a => a.Description))
            : null;
        var rows = candidates is not null
            ? from album in albums
              join rank in candidates.BestMatches() on album.Id equals rank.Item
              select new SearchRow<Album>
              {
                  Item = album,
                  Rank = rank.Rank,
                  Sim = rank.Sim,
                  SortAt = album.UpdatedAt,
                  Id = album.Id
              }
            : albums.Select(album => new SearchRow<Album>
            {
                Item = album,
                Rank = 0,
                Sim = 0d,
                SortAt = album.UpdatedAt,
                Id = album.Id
            });
        if (query.ResolveGuid is { } id)
            rows = rows.Where(r => r.Id == id);

        var pageQuery = rows.After(query.Cursor).InSearchOrder().Take(query.Take);
        if (candidates is not null)
            pageQuery = pageQuery.InSearchOrder().WithMatchCandidates(candidates, r => r.Item.Id);
        var page = await pageQuery.ToListAsync(cancellationToken);
        return page.Select(r => SimpleHit(SearchHitKind.Album, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Name, r.Item.Description, null,
            r.MatchField, r.MatchValue)).ToList();
    }

    private async Task<List<PendingHit>> LoadPlaylists(SectionQuery query, CancellationToken cancellationToken)
    {
        if (query.NothingToFind)
            return [];

        var ownerId = _userContext.UserId;
        var playlists = _context.MusicPlaylists.AsNoTracking().Where(x => x.OwnerId == ownerId);
        var candidates = query.Terms.HasQuery
            ? playlists.CandidatesBy(query.Terms, p => p.Id, "name", 0, p => p.Name)
                .Concat(playlists.CandidatesBy(query.Terms, p => p.Id, "description", 1, p => p.Description))
            : null;
        var rows = candidates is not null
            ? from playlist in playlists
              join rank in candidates.BestMatches() on playlist.Id equals rank.Item
              select new SearchRow<MusicPlaylist>
              {
                  Item = playlist,
                  Rank = rank.Rank,
                  Sim = rank.Sim,
                  SortAt = playlist.UpdatedAt,
                  Id = playlist.Id
              }
            : playlists.Select(playlist => new SearchRow<MusicPlaylist>
            {
                Item = playlist,
                Rank = 0,
                Sim = 0d,
                SortAt = playlist.UpdatedAt,
                Id = playlist.Id
            });
        if (query.ResolveGuid is { } id)
            rows = rows.Where(r => r.Id == id);

        var pageQuery = rows.After(query.Cursor).InSearchOrder().Take(query.Take);
        if (candidates is not null)
            pageQuery = pageQuery.InSearchOrder().WithMatchCandidates(candidates, r => r.Item.Id);
        var page = await pageQuery.ToListAsync(cancellationToken);
        return page.Select(r => SimpleHit(SearchHitKind.Playlist, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Name, r.Item.Description, null,
            r.MatchField, r.MatchValue)).ToList();
    }

    /// <summary>Папки: каталоги, пользовательские и системные умные папки.</summary>
    private async Task<List<PendingHit>> LoadFolders(SectionQuery query, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var result = new List<PendingHit>();

        if (query.Wants(SearchHitKind.Folder) && !query.NothingToFind)
        {
            var rows = _context.CloudDirectories.AsNoTracking().Where(x => x.OwnerId == ownerId)
                .RankedBy(query.Terms, d => d, d => d.Name)
                .Select(r => new SearchRow<CloudDirectory>
                {
                    Item = r.Item,
                    Rank = r.Rank,
                    Sim = r.Sim,
                    SortAt = r.Item.UpdatedAt,
                    Id = r.Item.Id,
                    MatchField = query.Terms.HasQuery ? "name" : string.Empty,
                    MatchValue = query.Terms.HasQuery ? r.Item.Name : string.Empty
                });
            if (query.ResolveGuid is { } id)
                rows = rows.Where(r => r.Id == id);

            var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
            result.AddRange(page.Select(r => SimpleHit(SearchHitKind.Folder, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Name, "Папка", null,
                r.MatchField, r.MatchValue)));
        }

        if (query.Wants(SearchHitKind.DynamicFolder))
        {
            if (!query.NothingToFind)
            {
                var rows = _context.DynamicFolders.AsNoTracking().Where(x => x.OwnerId == ownerId)
                    .RankedBy(query.Terms, f => f, f => f.Name)
                    .Select(r => new SearchRow<DynamicFolder>
                    {
                        Item = r.Item,
                        Rank = r.Rank,
                        Sim = r.Sim,
                        SortAt = r.Item.UpdatedAt,
                        Id = r.Item.Id,
                        MatchField = query.Terms.HasQuery ? "name" : string.Empty,
                        MatchValue = query.Terms.HasQuery ? r.Item.Name : string.Empty
                    });
                if (query.ResolveGuid is { } id)
                    rows = rows.Where(r => r.Id == id);

                var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
                result.AddRange(page.Select(r => SimpleHit(SearchHitKind.DynamicFolder, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Name, "Умная папка", null,
                    r.MatchField, r.MatchValue)));
            }

            var folders = SystemDynamicFolders.All();
            if (query.Terms.HasQuery)
            {
                var names = folders.Select(folder => folder.Name).ToArray();
                var ranked = await _context.Database.SqlQuery<string>($"SELECT unnest({names}) AS \"Value\"")
                    .RankedBy(query.Terms, name => name, name => name)
                    .ToListAsync(cancellationToken);
                var foldersByName = folders.ToDictionary(folder => folder.Name, StringComparer.Ordinal);
                foreach (var rank in ranked)
                {
                    var folder = foldersByName[rank.Item];
                    var key = folder.SystemKey ?? string.Empty;
                    if (query.IsResolve && key != query.ResolveId)
                        continue;
                    var hit = SimpleHit(SearchHitKind.DynamicFolder, key, rank.Rank, rank.Sim, folder.UpdatedAt, folder.Name,
                        "Системная умная папка", null, "name", folder.Name);
                    if (query.Cursor is null || IsAfter(hit, query.Cursor))
                        result.Add(hit);
                }
            }
            else if (query.IsResolve)
            {
                foreach (var folder in folders.Where(folder => folder.SystemKey == query.ResolveId))
                    result.Add(SimpleHit(SearchHitKind.DynamicFolder, folder.SystemKey ?? string.Empty, 0, 0d, folder.UpdatedAt, folder.Name,
                        "Системная умная папка", null, string.Empty, string.Empty));
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
                .RankedBy(terms, r => r, r => r.File.Filename)
                .Select(r => new SearchRow<SharedFileRow>
                {
                    Item = r.Item,
                    Rank = r.Rank,
                    Sim = r.Sim,
                    SortAt = r.Item.Grant.CreatedAt,
                    Id = r.Item.Grant.Id,
                    MatchField = terms.HasQuery ? "name" : string.Empty,
                    MatchValue = terms.HasQuery ? r.Item.File.Filename ?? string.Empty : string.Empty
                });
            if (query.ResolveGuid is { } id)
                rows = rows.Where(r => r.Id == id || r.Item.File.Id == id);

            var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
            result.AddRange(page.Select(r => SimpleHit(SearchHitKind.SharedFile, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.File.Filename ?? "Файл",
                "Доступный файл", r.Item.File.Id, r.MatchField, r.MatchValue)));
        }

        if (query.Wants(SearchHitKind.SharedFolder))
        {
            var rows = (from grant in _context.DirectoryGrants.AsNoTracking()
                        join dir in _context.CloudDirectories.AsNoTracking() on grant.DirectoryId equals dir.Id
                        where grant.RecipientId == recipientId
                        select new SharedFolderRow { Grant = grant, Directory = dir })
                .RankedBy(terms, r => r, r => r.Directory.Name)
                .Select(r => new SearchRow<SharedFolderRow>
                {
                    Item = r.Item,
                    Rank = r.Rank,
                    Sim = r.Sim,
                    SortAt = r.Item.Grant.CreatedAt,
                    Id = r.Item.Grant.Id,
                    MatchField = terms.HasQuery ? "name" : string.Empty,
                    MatchValue = terms.HasQuery ? r.Item.Directory.Name : string.Empty
                });
            if (query.ResolveGuid is { } id)
                rows = rows.Where(r => r.Id == id || r.Item.Directory.Id == id);

            var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
            result.AddRange(page.Select(r => SimpleHit(SearchHitKind.SharedFolder, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Directory.Name,
                "Доступная папка", r.Item.Directory.Id, r.MatchField, r.MatchValue)));
        }

        if (query.Wants(SearchHitKind.SharedPlaylist))
        {
            var rows = (from grant in _context.MusicPlaylistGrants.AsNoTracking()
                        join playlist in _context.MusicPlaylists.AsNoTracking() on grant.PlaylistId equals playlist.Id
                        where grant.RecipientId == recipientId
                        select new SharedPlaylistRow { Grant = grant, Playlist = playlist })
                .RankedBy(terms, r => r, r => r.Playlist.Name)
                .Select(r => new SearchRow<SharedPlaylistRow>
                {
                    Item = r.Item,
                    Rank = r.Rank,
                    Sim = r.Sim,
                    SortAt = r.Item.Grant.CreatedAt,
                    Id = r.Item.Grant.Id,
                    MatchField = terms.HasQuery ? "name" : string.Empty,
                    MatchValue = terms.HasQuery ? r.Item.Playlist.Name : string.Empty
                });
            if (query.ResolveGuid is { } id)
                rows = rows.Where(r => r.Id == id || r.Item.Playlist.Id == id);

            var page = await rows.After(query.Cursor).InSearchOrder().Take(query.Take).ToListAsync(cancellationToken);
            result.AddRange(page.Select(r => SimpleHit(SearchHitKind.SharedPlaylist, r.Id.ToString(), r.Rank, r.Sim, r.SortAt, r.Item.Playlist.Name,
                "Доступный плейлист", r.Item.Playlist.Id, r.MatchField, r.MatchValue)));
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
