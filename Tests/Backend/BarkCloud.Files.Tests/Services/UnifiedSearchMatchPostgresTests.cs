using BarkCloud.Files.Domain;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

using Npgsql;
using Xunit;
using Xunit.Abstractions;
using MediaKind = BarkCloud.Files.Domain.MediaKind;
using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Tests.Services;

public sealed class UnifiedSearchMatchPostgresTests(ITestOutputHelper output)
{
    private const long Owner = 42;
    private const long OtherOwner = 84;

    [PostgresFact]
    public async Task Search_FolderMatchField_UsesSqlMatchSemantics()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var folder = new CloudDirectory
        {
            Id = Guid.NewGuid(),
            OwnerId = Owner,
            Name = "report",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await using (var seed = db.CreateContext())
        {
            seed.CloudDirectories.Add(folder);
            await seed.SaveChangesAsync();
        }

        const string query = "quarterly report finances";
        await using var context = db.CreateContext();
        var service = new UnifiedSearchService(context, UserContextFactory.Create(Owner),
            new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty());
        var response = await service.Search(new SearchRequest
        {
            Query = query,
            Pages = { new SearchSectionPage { Section = SearchSection.Folders, Limit = 20 } }
        }, default);
        var hit = response.Sections.Single().Hits.Single(x => x.Id == folder.Id.ToString());

        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT word_similarity('report', 'quarterly report finances')", connection);
        var sqlSimilarity = (float)(await command.ExecuteScalarAsync())!;
        output.WriteLine($"hit={hit.Id}; match_field={hit.MatchField}; match_value={hit.MatchValue}; word_similarity={sqlSimilarity}");

        Assert.Equal(1f, sqlSimilarity);
        Assert.Equal("name", hit.MatchField);
        Assert.Equal("report", hit.MatchValue);
    }

    [PostgresFact]
    public async Task Search_MultitermQueryUsesSqlFieldAcrossSources()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var aliasFile = NewFile(MediaKind.Photo, "alias-photo.jpg");
        var tagFile = NewFile(MediaKind.Photo, "tag-photo.jpg");
        var trackFile = NewFile(MediaKind.Audio, "artist-track.mp3");
        var documentFile = NewFile(MediaKind.Document, "document.pdf");
        var trashFile = NewFile(MediaKind.Photo, "trash.jpg");
        var sharedFile = NewFile(MediaKind.Other, "report", OtherOwner);
        var directory = NewFolder("report", Owner);
        var sharedDirectory = NewFolder("report", OtherOwner);
        var dynamicFolder = NewDynamicFolder("report", Owner);
        var album = NewAlbum("misc", "report", Owner);
        var playlist = NewPlaylist("mix", "report", Owner);
        var sharedPlaylist = NewPlaylist("report", null, OtherOwner);
        var tagEntry = NewEntry(tagFile, "tag-cover");
        var aliasEntry = NewEntry(aliasFile, "alias-cover");
        var trackEntry = NewEntry(trackFile, "artist-track");
        var documentEntry = NewEntry(documentFile, "document-file");
        var trashEntry = NewEntry(trashFile, "report", deleted: true);

        await Seed(db,
            aliasFile, tagFile, trackFile, documentFile, trashFile, sharedFile,
            aliasEntry, tagEntry, trackEntry, documentEntry, trashEntry,
            new FileSearchAlias { OwnerId = Owner, FileId = aliasFile.Id, Value = "Quarterly Report", NormalizedValue = "quarterly report", UpdatedAt = DateTime.UtcNow },
            new FileTag { OwnerId = Owner, FileId = tagFile.Id, Value = "Report", NormalizedValue = "report", CreatedAt = DateTime.UtcNow },
            new FileMetadata { FileId = trackFile.Id, AudioArtist = "report", CreatedAt = DateTime.UtcNow },
            new FileMetadata { FileId = documentFile.Id, DocumentTitle = "report", CreatedAt = DateTime.UtcNow },
            directory, sharedDirectory, dynamicFolder, album, playlist, sharedPlaylist,
            new FileGrant { Id = Guid.NewGuid(), OwnerId = OtherOwner, RecipientId = Owner, FileId = sharedFile.Id, CreatedAt = DateTime.UtcNow },
            new DirectoryGrant { Id = Guid.NewGuid(), OwnerId = OtherOwner, RecipientId = Owner, DirectoryId = sharedDirectory.Id, CreatedAt = DateTime.UtcNow },
            new MusicPlaylistGrant { Id = Guid.NewGuid(), OwnerId = OtherOwner, RecipientId = Owner, PlaylistId = sharedPlaylist.Id, CreatedAt = DateTime.UtcNow });

        foreach (var query in new[] { "quarterly report finances", "report" })
        {
            var photos = await Search(db, SearchSection.Photos, query);
            AssertEveryHitHasMatch(photos);
            AssertMatch(photos.Hits.Single(hit => hit.FileId == aliasFile.Id.ToString()), "alias", "Quarterly Report");
            AssertMatch(photos.Hits.Single(hit => hit.FileId == tagFile.Id.ToString()), "tag", "Report");

            var tracks = await Search(db, SearchSection.Tracks, query);
            AssertEveryHitHasMatch(tracks);
            AssertMatch(tracks.Hits.Single(hit => hit.FileId == trackFile.Id.ToString()), "artist", "report");

            var files = await Search(db, SearchSection.Files, query);
            AssertEveryHitHasMatch(files);
            AssertMatch(files.Hits.Single(hit => hit.FileId == documentFile.Id.ToString()), "documentTitle", "report");

            var albums = await Search(db, SearchSection.Albums, query);
            AssertEveryHitHasMatch(albums);
            AssertMatch(albums.Hits.Single(hit => hit.Id == album.Id.ToString()), "description", "report");

            var playlists = await Search(db, SearchSection.Playlists, query);
            AssertEveryHitHasMatch(playlists);
            AssertMatch(playlists.Hits.Single(hit => hit.Id == playlist.Id.ToString()), "description", "report");

            var folders = await Search(db, SearchSection.Folders, query);
            AssertEveryHitHasMatch(folders);
            AssertMatch(folders.Hits.Single(hit => hit.Id == directory.Id.ToString()), "name", "report");
            AssertMatch(folders.Hits.Single(hit => hit.Id == dynamicFolder.Id.ToString()), "name", "report");

            var shared = await Search(db, SearchSection.Shared, query);
            AssertEveryHitHasMatch(shared);
            AssertMatch(shared.Hits.Single(hit => hit.Kind == SearchHitKind.SharedFile), "name", "report");
            AssertMatch(shared.Hits.Single(hit => hit.Kind == SearchHitKind.SharedFolder), "name", "report");
            AssertMatch(shared.Hits.Single(hit => hit.Kind == SearchHitKind.SharedPlaylist), "name", "report");

            var trash = await Search(db, SearchSection.Trash, query);
            AssertEveryHitHasMatch(trash);
            AssertMatch(trash.Hits.Single(hit => hit.FileId == trashFile.Id.ToString()), "name", "report");
        }
    }

    [PostgresFact]
    public async Task Search_UsesSqlThresholdAndDisablesTrigramsForShortQueries()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var above = NewFolder("repro", Owner);
        var below = NewFolder("raport", Owner);
        var shortTypo = NewFolder("car", Owner);
        var shortSubstring = NewFolder("catapult", Owner);
        await Seed(db, above, below, shortTypo, shortSubstring);

        var aboveScore = await WordSimilarity(db, "repro", "report");
        var belowScore = await WordSimilarity(db, "raport", "report");
        Assert.True(aboveScore > .45f, $"PostgreSQL word_similarity={aboveScore}");
        Assert.True(belowScore < .45f, $"PostgreSQL word_similarity={belowScore}");
        var report = await Search(db, SearchSection.Folders, "report");
        Assert.Collection(report.Hits, hit =>
        {
            Assert.Equal(above.Id.ToString(), hit.Id);
            AssertMatch(hit, "name", "repro");
        });

        var shortScore = await WordSimilarity(db, "car", "cat");
        Assert.True(shortScore > .45f, $"SQL would accept this typo if trigram matching were enabled: {shortScore}");
        var shortQuery = await Search(db, SearchSection.Folders, "cat");
        Assert.Collection(shortQuery.Hits, hit =>
        {
            Assert.Equal(shortSubstring.Id.ToString(), hit.Id);
            AssertMatch(hit, "name", "catapult");
        });
        Assert.DoesNotContain(shortQuery.Hits, hit => hit.Id == shortTypo.Id.ToString());
    }

    [PostgresFact]
    public async Task Search_MultifieldWinnerUsesRankSimilarityPriorityAndUtf8Order()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var rankFile = NewFile(MediaKind.Photo, "rank.jpg");
        var tieFile = NewFile(MediaKind.Photo, "tie.jpg");
        var unicodeFile = NewFile(MediaKind.Photo, "unicode.jpg");
        var similarityFile = NewFile(MediaKind.Photo, "similarity.jpg");
        var rankEntry = NewEntry(rankFile, "repot");
        var tieEntry = NewEntry(tieFile, "report");
        var unicodeEntry = NewEntry(unicodeFile, "unrelated");
        var similarityEntry = NewEntry(similarityFile, "unrelated");
        var album = NewAlbum("report", "report", Owner);

        await Seed(db,
            rankFile, tieFile, unicodeFile, similarityFile,
            rankEntry, tieEntry, unicodeEntry, similarityEntry, album,
            new FileTag { OwnerId = Owner, FileId = rankFile.Id, Value = "report", NormalizedValue = "report", CreatedAt = DateTime.UtcNow },
            new FileSearchAlias { OwnerId = Owner, FileId = tieFile.Id, Value = "report", NormalizedValue = "report", UpdatedAt = DateTime.UtcNow },
            new FileTag { OwnerId = Owner, FileId = tieFile.Id, Value = "report", NormalizedValue = "report", CreatedAt = DateTime.UtcNow },
            new FileTag { OwnerId = Owner, FileId = unicodeFile.Id, Value = "report\uE000", NormalizedValue = "report\uE000", CreatedAt = DateTime.UtcNow },
            new FileTag { OwnerId = Owner, FileId = unicodeFile.Id, Value = "report😀", NormalizedValue = "report😀", CreatedAt = DateTime.UtcNow },
            new FileTag { OwnerId = Owner, FileId = similarityFile.Id, Value = "repro", NormalizedValue = "repro", CreatedAt = DateTime.UtcNow },
            new FileTag { OwnerId = Owner, FileId = similarityFile.Id, Value = "repot", NormalizedValue = "repot", CreatedAt = DateTime.UtcNow });

        var report = await Search(db, SearchSection.Photos, "report");
        AssertMatch(report.Hits.Single(hit => hit.FileId == rankFile.Id.ToString()), "tag", "report");
        AssertMatch(report.Hits.Single(hit => hit.FileId == tieFile.Id.ToString()), "name", "report");
        AssertMatch(report.Hits.Single(hit => hit.FileId == unicodeFile.Id.ToString()), "tag", "report\uE000");
        AssertMatch(report.Hits.Single(hit => hit.FileId == similarityFile.Id.ToString()), "tag", "repot");
        AssertMatch((await Search(db, SearchSection.Albums, "report")).Hits.Single(), "name", "report");

        var reproScore = await WordSimilarity(db, "repro", "report");
        var repotScore = await WordSimilarity(db, "repot", "report");
        Assert.True(repotScore > reproScore, $"Expected SQL similarity {repotScore} > {reproScore}");
    }

    [PostgresFact]
    public async Task Search_UnicodeAndNormalizedMetadataKeepSqlBehavior()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var cyrillic = NewFolder("отчёт", Owner);
        var ordinary = NewFolder("report", Owner);
        var fullwidth = NewFolder("ｒｅｐｏｒｔ", Owner);
        var decomposed = NewFolder("cafe\u0301", Owner);
        var spaced = NewFolder("quarterly  report", Owner);
        var aliasFile = NewFile(MediaKind.Photo, "alias.jpg");
        var tagFile = NewFile(MediaKind.Photo, "tag.jpg");
        await Seed(db,
            cyrillic, ordinary, fullwidth, decomposed, spaced, aliasFile, tagFile,
            NewEntry(aliasFile, "alias-item"), NewEntry(tagFile, "tag-item"),
            new FileSearchAlias { OwnerId = Owner, FileId = aliasFile.Id, Value = "Quarterly Report", NormalizedValue = "quarterly report", UpdatedAt = DateTime.UtcNow },
            new FileTag { OwnerId = Owner, FileId = tagFile.Id, Value = "Quarterly Report", NormalizedValue = "quarterly report", CreatedAt = DateTime.UtcNow });

        Assert.Equal(1f, await WordSimilarity(db, "отчёт", "отчёт"));
        var cyrillicHits = await Search(db, SearchSection.Folders, "ОТЧЁТ");
        AssertMatch(Assert.Single(cyrillicHits.Hits), "name", "отчёт");

        Assert.Equal(1f, await WordSimilarity(db, "report", SearchText.Normalize("ｒｅｐｏｒｔ")));
        var normalizedQuery = await Search(db, SearchSection.Folders, "ｒｅｐｏｒｔ");
        AssertMatch(normalizedQuery.Hits.Single(hit => hit.Id == ordinary.Id.ToString()), "name", "report");

        Assert.Equal(0f, await WordSimilarity(db, "ｒｅｐｏｒｔ", "report"));
        var rawFullwidthName = await Search(db, SearchSection.Folders, "report");
        Assert.DoesNotContain(rawFullwidthName.Hits, hit => hit.Id == fullwidth.Id.ToString());

        var composedScore = await WordSimilarity(db, "cafe\u0301", "café");
        Assert.True(composedScore > .45f, $"PostgreSQL word_similarity={composedScore}");
        Assert.False(await ILike(db, "cafe\u0301", "%café%"));
        var composedHits = await Search(db, SearchSection.Folders, "café");
        AssertMatch(composedHits.Hits.Single(hit => hit.Id == decomposed.Id.ToString()), "name", "cafe\u0301");

        Assert.Equal(1f, await WordSimilarity(db, "quarterly  report", "quarterly report"));
        Assert.False(await ILike(db, "quarterly  report", "%quarterly report%"));
        var spacedHits = await Search(db, SearchSection.Folders, "quarterly report");
        AssertMatch(spacedHits.Hits.Single(hit => hit.Id == spaced.Id.ToString()), "name", "quarterly  report");

        Assert.Equal(1f, await WordSimilarity(db, "quarterly report", "quarterly report"));
        var metadataHits = await Search(db, SearchSection.Photos, "QUARTERLY REPORT");
        AssertMatch(metadataHits.Hits.Single(hit => hit.FileId == aliasFile.Id.ToString()), "alias", "Quarterly Report");
        AssertMatch(metadataHits.Hits.Single(hit => hit.FileId == tagFile.Id.ToString()), "tag", "Quarterly Report");
    }

    [PostgresFact]
    public async Task Search_SpecialCharactersAreLiteralLikeValues()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var percent = NewFolder("a%b", Owner);
        var percentNoise = NewFolder("axb", Owner);
        var underscore = NewFolder("a_b", Owner);
        var underscoreNoise = NewFolder("acb", Owner);
        var slash = NewFolder("a\\b", Owner);
        var slashNoise = NewFolder("a/cb", Owner);
        var punctuation = NewFolder("a.b", Owner);
        var punctuationNoise = NewFolder("aab", Owner);
        await Seed(db, percent, percentNoise, underscore, underscoreNoise, slash, slashNoise, punctuation, punctuationNoise);

        Assert.Equal(percent.Id.ToString(), Assert.Single((await Search(db, SearchSection.Folders, "a%")).Hits).Id);
        Assert.Equal(underscore.Id.ToString(), Assert.Single((await Search(db, SearchSection.Folders, "a_")).Hits).Id);
        Assert.Equal(slash.Id.ToString(), Assert.Single((await Search(db, SearchSection.Folders, "a\\")).Hits).Id);
        Assert.Equal(punctuation.Id.ToString(), Assert.Single((await Search(db, SearchSection.Folders, "a.")).Hits).Id);
    }

    [PostgresFact]
    public async Task Search_FolderCursorTraversalMatchesSinglePageWithSystemFolder()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        const string systemName = "Недавние документы";
        const string query = "недавние документы";
        var first = NewFolder(systemName + " alpha", Owner);
        var second = NewFolder(systemName + " beta", Owner);
        var dynamic = NewDynamicFolder(systemName + " custom", Owner);
        await Seed(db, first, second, dynamic);

        Assert.Equal(1f, await WordSimilarity(db, systemName, query));
        var all = await Search(db, SearchSection.Folders, query, 50);
        var systemHit = Assert.Single(all.Hits, hit => hit.Id == SystemDynamicFolders.KeyRecentDocs);
        AssertMatch(systemHit, "name", systemName);
        foreach (var pageSize in new[] { 1, 2, 3 })
        {
            var traversed = await Traverse(db, SearchSection.Folders, query, pageSize);
            Assert.Equal(all.Hits.Select(hit => (hit.Kind, hit.Id, hit.MatchField, hit.MatchValue)),
                traversed.Select(hit => (hit.Kind, hit.Id, hit.MatchField, hit.MatchValue)));
        }
    }

    [PostgresFact]
    public async Task Search_MultifieldCursorTraversalMatchesSinglePage()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        const string query = "quarterly report finances";
        var aliasFile = NewFile(MediaKind.Photo, "alias.jpg");
        var firstTagFile = NewFile(MediaKind.Photo, "first-tag.jpg");
        var secondTagFile = NewFile(MediaKind.Photo, "second-tag.jpg");
        var nameFile = NewFile(MediaKind.Photo, "name.jpg");
        await Seed(db,
            aliasFile, firstTagFile, secondTagFile, nameFile,
            NewEntry(aliasFile, "alias-entry"), NewEntry(firstTagFile, "first-tag-entry"),
            NewEntry(secondTagFile, "second-tag-entry"), NewEntry(nameFile, "report"),
            new FileSearchAlias { OwnerId = Owner, FileId = aliasFile.Id, Value = "Quarterly Report", NormalizedValue = "quarterly report", UpdatedAt = DateTime.UtcNow },
            new FileTag { OwnerId = Owner, FileId = firstTagFile.Id, Value = "report", NormalizedValue = "report", CreatedAt = DateTime.UtcNow },
            new FileTag { OwnerId = Owner, FileId = secondTagFile.Id, Value = "quarterly report", NormalizedValue = "quarterly report", CreatedAt = DateTime.UtcNow });

        Assert.Equal(1f, await WordSimilarity(db, "report", query));
        var all = await Search(db, SearchSection.Photos, query, 50);
        Assert.Equal(4, all.Hits.Count);
        AssertMatch(all.Hits.Single(hit => hit.FileId == aliasFile.Id.ToString()), "alias", "Quarterly Report");
        AssertMatch(all.Hits.Single(hit => hit.FileId == firstTagFile.Id.ToString()), "tag", "report");
        AssertMatch(all.Hits.Single(hit => hit.FileId == secondTagFile.Id.ToString()), "tag", "quarterly report");
        AssertMatch(all.Hits.Single(hit => hit.FileId == nameFile.Id.ToString()), "name", "report");

        foreach (var pageSize in new[] { 1, 2, 3 })
        {
            var traversed = await Traverse(db, SearchSection.Photos, query, pageSize);
            Assert.Equal(all.Hits.Select(hit => (hit.Kind, hit.Id, hit.MatchField, hit.MatchValue)),
                traversed.Select(hit => (hit.Kind, hit.Id, hit.MatchField, hit.MatchValue)));
        }
    }

    [PostgresFact]
    public async Task ResolveHit_LeavesMatchSignatureEmpty()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var folder = NewFolder("report", Owner);
        await Seed(db, folder);
        await using var context = db.CreateContext();
        var service = CreateService(context);

        var hit = await service.ResolveHit(new SearchHitReference { Kind = SearchHitKind.Folder, Id = folder.Id.ToString() }, default);

        Assert.Equal(string.Empty, hit.MatchField);
        Assert.Equal(string.Empty, hit.MatchValue);
    }

    private static async Task<SearchSectionResult> Search(PostgresFilesDatabase db, SearchSection section, string query, int limit = 50, string cursor = "")
    {
        await using var context = db.CreateContext();
        var response = await CreateService(context).Search(new SearchRequest
        {
            Query = query,
            Pages = { new SearchSectionPage { Section = section, Limit = limit, Cursor = cursor } }
        }, default);
        return response.Sections.Single();
    }

    private static async Task<List<SearchHit>> Traverse(PostgresFilesDatabase db, SearchSection section, string query, int pageSize)
    {
        var hits = new List<SearchHit>();
        var cursor = string.Empty;
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var page = await Search(db, section, query, pageSize, cursor);
            hits.AddRange(page.Hits);
            if (!page.HasMore)
            {
                Assert.Empty(page.NextCursor);
                return hits;
            }
            Assert.Equal(pageSize, page.Hits.Count);
            Assert.NotEmpty(page.NextCursor);
            cursor = page.NextCursor;
        }
        throw new InvalidOperationException("Курсор поиска не завершился.");
    }

    private static UnifiedSearchService CreateService(BarkCloud.Files.Persistence.FilesContext context) => new(
        context,
        UserContextFactory.Create(Owner),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 },
        TestConfiguration.Empty());

    private static void AssertMatch(SearchHit hit, string field, string value)
    {
        Assert.False(string.IsNullOrEmpty(hit.MatchField));
        Assert.False(string.IsNullOrEmpty(hit.MatchValue));
        Assert.Equal(field, hit.MatchField);
        Assert.Equal(value, hit.MatchValue);
    }

    private static void AssertEveryHitHasMatch(SearchSectionResult results)
    {
        foreach (var hit in results.Hits)
        {
            Assert.False(string.IsNullOrEmpty(hit.MatchField), $"{hit.Kind}/{hit.Id} не содержит match_field");
            Assert.False(string.IsNullOrEmpty(hit.MatchValue), $"{hit.Kind}/{hit.Id} не содержит match_value");
        }
    }

    private static UploadFile NewFile(BarkCloud.Files.Domain.MediaKind kind, string filename, long ownerId = Owner)
    {
        var now = DateTime.UtcNow;
        return new UploadFile
        {
            Id = Guid.NewGuid(),
            Uploaders = [ownerId],
            Type = UploadFileType.CloudFile,
            MediaKind = kind,
            Filename = filename,
            StorageProfileId = "test-storage",
            Size = 1234,
            CreatedAt = now,
            UploadedAt = now,
            Etag = "etag"
        };
    }

    private static CloudFileEntry NewEntry(UploadFile file, string name, long ownerId = Owner, bool deleted = false)
    {
        var now = DateTime.UtcNow;
        return new CloudFileEntry
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            DirectoryId = Guid.NewGuid(),
            FileId = file.Id,
            Name = name,
            CreatedAt = now,
            IsDeleted = deleted,
            DeletedAt = deleted ? now : null,
            PurgeAt = deleted ? now.AddDays(30) : null
        };
    }

    private static CloudDirectory NewFolder(string name, long ownerId)
    {
        var now = DateTime.UtcNow;
        return new CloudDirectory { Id = Guid.NewGuid(), OwnerId = ownerId, Name = name, CreatedAt = now, UpdatedAt = now };
    }

    private static DynamicFolder NewDynamicFolder(string name, long ownerId)
    {
        var now = DateTime.UtcNow;
        return new DynamicFolder { Id = Guid.NewGuid(), OwnerId = ownerId, Name = name, CreatedAt = now, UpdatedAt = now };
    }

    private static Album NewAlbum(string name, string? description, long ownerId)
    {
        var now = DateTime.UtcNow;
        return new Album { Id = Guid.NewGuid(), OwnerId = ownerId, Name = name, Description = description, CreatedAt = now, UpdatedAt = now };
    }

    private static MusicPlaylist NewPlaylist(string name, string? description, long ownerId)
    {
        var now = DateTime.UtcNow;
        return new MusicPlaylist { Id = Guid.NewGuid(), OwnerId = ownerId, Name = name, Description = description, CreatedAt = now, UpdatedAt = now };
    }

    private static async Task Seed(PostgresFilesDatabase db, params object[] entities)
    {
        await using var context = db.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private static async Task<float> WordSimilarity(PostgresFilesDatabase db, string value, string query)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT word_similarity($1, $2)", connection);
        command.Parameters.AddWithValue(value);
        command.Parameters.AddWithValue(query);
        return (float)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ILike(PostgresFilesDatabase db, string value, string pattern)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT $1 ILIKE $2", connection);
        command.Parameters.AddWithValue(value);
        command.Parameters.AddWithValue(pattern);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
