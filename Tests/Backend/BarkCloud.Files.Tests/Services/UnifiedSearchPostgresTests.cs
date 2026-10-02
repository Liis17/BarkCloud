using System.Data.Common;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

using Grpc.Core;

using Microsoft.EntityFrameworkCore.Diagnostics;

using MediaKind = BarkCloud.Files.Domain.MediaKind;
using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Tests.Services;

/// <summary>
/// F20: единый поиск на реальном PostgreSQL (ILIKE и trigram в SQLite недоступны). Каталог сидится один раз;
/// проверяются ранжирование и видимость, обход курсором, точечное разрешение хитов и то, что объём прочитанных
/// из БД строк зависит от лимита страницы, а не от размера каталога.
/// </summary>
public sealed class UnifiedSearchPostgresTests(SearchCatalogFixture fixture) : IClassFixture<SearchCatalogFixture>
{
    private const string Query = "report";

    [PostgresFact]
    public async Task Search_Photos_RanksByMatchQualityThenRecency()
    {
        var c = fixture.Catalog;

        var result = await Find(SearchSection.Photos, Query);

        result.Hits.Select(x => x.Id).Should().Equal(Ids(
            c.TagExact.EntryId, c.Exact.EntryId,
            c.PrefixNew.EntryId, c.NoEntry.FileId, c.PrefixOld.EntryId,
            c.AliasSub.EntryId, c.Sub.EntryId,
            c.Typo.EntryId));
        result.HasMore.Should().BeFalse();
        result.NextCursor.Should().BeEmpty();
        var byId = result.Hits.ToDictionary(x => x.Id);
        byId[c.TagExact.EntryId.ToString()].MatchField.Should().Be("tag");
        byId[c.TagExact.EntryId.ToString()].MatchValue.Should().Be("report");
        byId[c.AliasSub.EntryId.ToString()].MatchField.Should().Be("alias");
        byId[c.Exact.EntryId.ToString()].MatchField.Should().Be("name");
        byId[c.Typo.EntryId.ToString()].MatchField.Should().Be("name");
    }

    [PostgresFact]
    public async Task Search_Photos_FillsHitCard()
    {
        var c = fixture.Catalog;

        var result = await Find(SearchSection.Photos, Query);

        var exact = result.Hits.Single(x => x.Id == c.Exact.EntryId.ToString());
        exact.Kind.Should().Be(SearchHitKind.Photo);
        exact.FileId.Should().Be(c.Exact.FileId.ToString());
        exact.EntryId.Should().Be(c.Exact.EntryId.ToString());
        exact.Title.Should().Be("Report");
        exact.Favorite.Should().BeTrue();
        exact.Size.Should().Be(1234);
        exact.PreviewUrl.Should().Be($"http://localhost:7026/download/{c.ExactPreview512}");
        exact.CreatedAt.ToDateTime().Should().Be(c.Exact.CreatedAt);

        var tagged = result.Hits.Single(x => x.Id == c.TagExact.EntryId.ToString());
        tagged.Favorite.Should().BeFalse("избранное другого пользователя не принадлежит мне");
        tagged.PreviewUrl.Should().BeEmpty();

        var noEntry = result.Hits.Single(x => x.Id == c.NoEntry.FileId.ToString());
        noEntry.EntryId.Should().BeEmpty();
        noEntry.Title.Should().Be("report-noentry.jpg");
    }

    [PostgresFact]
    public async Task Search_Videos_RanksByMatchQualityThenRecency()
    {
        var c = fixture.Catalog;

        var result = await Find(SearchSection.Videos, Query);

        result.Hits.Select(x => x.Id).Should().Equal(Ids(c.VideoExact.EntryId, c.VideoSub.EntryId));
        result.Hits.Should().OnlyContain(x => x.Kind == SearchHitKind.Video);
    }

    [PostgresFact]
    public async Task Search_Tracks_UsesAudioMetadataOnly()
    {
        var c = fixture.Catalog;

        var result = await Find(SearchSection.Tracks, Query);

        // точное имя → префикс альбома → подстрока в исполнителе (новее) → подстрока в названии.
        result.Hits.Select(x => x.Id).Should().Equal(Ids(
            c.TrackExact.EntryId, c.TrackAlbum.EntryId, c.TrackArtist.EntryId, c.TrackTitle.EntryId));
        var byId = result.Hits.ToDictionary(x => x.Id);
        byId[c.TrackAlbum.EntryId.ToString()].MatchField.Should().Be("album");
        byId[c.TrackArtist.EntryId.ToString()].MatchField.Should().Be("artist");
        byId[c.TrackArtist.EntryId.ToString()].Subtitle.Should().Be("The Report Band");
        byId[c.TrackTitle.EntryId.ToString()].MatchField.Should().Be("title");
        result.Hits.Select(x => x.Id).Should().NotContain(c.TrackWithDocumentTitle.EntryId.ToString(),
            "документные поля учитываются только у документов");
    }

    [PostgresFact]
    public async Task Search_Files_ExcludesMediaAndUsesDocumentMetadataOnlyForDocuments()
    {
        var c = fixture.Catalog;

        var result = await Find(SearchSection.Files, Query);

        result.Hits.Select(x => x.Id).Should().Equal(Ids(
            c.DocExact.EntryId, c.OtherPrefix.EntryId, c.DocAuthor.EntryId, c.DocTitle.EntryId));
        var byId = result.Hits.ToDictionary(x => x.Id);
        byId[c.DocTitle.EntryId.ToString()].MatchField.Should().Be("documentTitle");
        byId[c.DocTitle.EntryId.ToString()].Subtitle.Should().Be("Quarterly Report");
        byId[c.DocAuthor.EntryId.ToString()].MatchField.Should().Be("documentAuthor");
        result.Hits.Select(x => x.Id).Should().NotContain(c.OtherWithDocumentTitle.EntryId.ToString());
        result.Hits.Should().OnlyContain(x => x.Kind == SearchHitKind.File);
    }

    [PostgresFact]
    public async Task Search_Trash_UsesLatestDeletedEntryOfFilesWithoutLiveEntry()
    {
        var c = fixture.Catalog;

        var result = await Find(SearchSection.Trash, Query);

        result.Hits.Select(x => x.Id).Should().Equal(Ids(
            c.TrashTwoEntriesMatching.EntryId, // удалён 80
            c.TrashDoc.EntryId,                // удалён 61
            c.TrashPhoto.EntryId,              // удалён 60
            c.TrashAudioArtist.EntryId));      // подстрока (ранг ниже)
        result.Hits.Should().OnlyContain(x => x.Kind == SearchHitKind.Trash);
        result.Hits.Single(x => x.Id == c.TrashPhoto.EntryId.ToString()).Subtitle.Should().Be("В корзине");
        result.Hits.Single(x => x.Id == c.TrashPhoto.EntryId.ToString()).CreatedAt.ToDateTime()
            .Should().Be(c.TrashPhoto.DeletedAt);
    }

    [PostgresFact]
    public async Task Search_AlbumsAndPlaylists_MatchNameAndDescription()
    {
        var c = fixture.Catalog;

        var albums = await Find(SearchSection.Albums, Query);
        var playlists = await Find(SearchSection.Playlists, Query);

        albums.Hits.Select(x => x.Id).Should().Equal(Ids(c.AlbumExact, c.AlbumPrefix, c.AlbumDescription));
        albums.Hits.First().MatchField.Should().Be("name");
        albums.Hits.Last().MatchField.Should().Be("description");
        playlists.Hits.Select(x => x.Id).Should().Equal(Ids(c.PlaylistPrefix, c.PlaylistDescription));
    }

    [PostgresFact]
    public async Task Search_Folders_MergesDirectoriesAndDynamicFolders()
    {
        var c = fixture.Catalog;

        var result = await Find(SearchSection.Folders, Query);

        result.Hits.Select(x => (x.Kind, x.Id)).Should().Equal(
            (SearchHitKind.DynamicFolder, c.DynamicExact.ToString()),
            (SearchHitKind.Folder, c.DirPrefix.ToString()),
            (SearchHitKind.Folder, c.DirSub.ToString()));
    }

    [PostgresFact]
    public async Task Search_Folders_IncludesSystemDynamicFolders()
    {
        var c = fixture.Catalog;

        var result = await Find(SearchSection.Folders, "недав");

        result.Hits.Select(x => (x.Kind, x.Id)).Should().Equal(
            (SearchHitKind.Folder, c.DirRecent.ToString()),
            (SearchHitKind.DynamicFolder, SystemDynamicFolders.KeyRecentMedia),
            (SearchHitKind.DynamicFolder, SystemDynamicFolders.KeyRecentDocs));
        result.Hits.Last().Subtitle.Should().Be("Системная умная папка");
    }

    [PostgresFact]
    public async Task Search_Shared_MergesGrantsOfAllKindsForRecipientOnly()
    {
        var c = fixture.Catalog;

        var result = await Find(SearchSection.Shared, Query);

        result.Hits.Select(x => (x.Kind, x.Id)).Should().Equal(
            (SearchHitKind.SharedFolder, c.DirGrant.ToString()),
            (SearchHitKind.SharedPlaylist, c.PlaylistGrant.ToString()),
            (SearchHitKind.SharedFile, c.FileGrant.ToString()));
        result.Hits.First().FileId.Should().Be(c.SharedDirId.ToString());
        result.Hits.Last().FileId.Should().Be(c.SharedFileId.ToString());
        result.Hits.Last().Title.Should().Be("report-shared.pdf");
    }

    [PostgresFact]
    public async Task Search_AllSections_DoesNotExposeForeignOrUnfinishedData()
    {
        var c = fixture.Catalog;

        var response = await CreateService(Catalog.Owner).Search(new SearchRequest { Query = Query }, default);

        response.Sections.Select(x => x.Section).Should().Equal(
            SearchSection.Photos, SearchSection.Videos, SearchSection.Files, SearchSection.Tracks, SearchSection.Albums,
            SearchSection.Playlists, SearchSection.Folders, SearchSection.Shared, SearchSection.Trash);
        var titles = response.Sections.SelectMany(x => x.Hits).Select(x => x.Title).ToList();
        titles.Should().NotContain(c.HiddenTitles);
        response.Sections.SelectMany(x => x.Hits).Select(x => x.FileId).Should().NotContain(c.HiddenFileIds.Select(x => x.ToString()));
        var photos = response.Sections.Single(x => x.Section == SearchSection.Photos);
        photos.Hits.Select(x => x.Id).Should().NotContain(c.PhotoWithForeignAlias.EntryId.ToString(),
            "алиас другого пользователя не должен находить мой файл");
    }

    [PostgresFact]
    public async Task Search_PagedTraversal_EqualsSinglePageForEverySection()
    {
        foreach (var section in new[]
                 {
                     SearchSection.Photos, SearchSection.Videos, SearchSection.Files, SearchSection.Tracks, SearchSection.Albums,
                     SearchSection.Playlists, SearchSection.Folders, SearchSection.Shared, SearchSection.Trash
                 })
        {
            var all = await Find(section, Query);
            foreach (var pageSize in new[] { 1, 2, 3 })
            {
                var traversed = await Traverse(CreateService(Catalog.Owner), section, Query, pageSize);

                traversed.Select(x => (x.Kind, x.Id)).Should().Equal(all.Hits.Select(x => (x.Kind, x.Id)),
                    $"{section} постранично по {pageSize}");
            }
        }
    }

    [PostgresFact]
    public async Task Search_PageBoundary_SetsHasMoreAndCursorOnlyWhenMoreHitsExist()
    {
        var service = CreateService(Catalog.Owner);

        var first = await Find(service, SearchSection.Photos, Query, limit: 3);
        var rest = await Find(service, SearchSection.Photos, Query, limit: 50, cursor: first.NextCursor);

        first.Hits.Should().HaveCount(3);
        first.HasMore.Should().BeTrue();
        first.NextCursor.Should().NotBeEmpty();
        rest.Hits.Should().HaveCount(5);
        rest.HasMore.Should().BeFalse();
        rest.NextCursor.Should().BeEmpty();
        var exactlyAll = await Find(service, SearchSection.Photos, Query, limit: 8);
        exactlyAll.HasMore.Should().BeFalse("ровно limit результатов — следующей страницы нет");
    }

    [PostgresFact]
    public async Task Search_MalformedCursor_ReturnsInvalidArgument()
    {
        foreach (var cursor in new[] { "!!!", "YWJj", Convert.ToBase64String("1|x|2|Photo|id"u8.ToArray()) })
        {
            var act = () => Find(CreateService(Catalog.Owner), SearchSection.Photos, Query, cursor: cursor);

            var exception = await act.Should().ThrowAsync<RpcException>();
            exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument, cursor);
        }
    }

    [PostgresFact]
    public async Task Search_QueryTooShort_ReturnsEmptySections()
    {
        var response = await CreateService(Catalog.Owner).Search(new SearchRequest { Query = "r" }, default);

        response.Sections.Should().HaveCount(9);
        response.Sections.Should().OnlyContain(x => x.Hits.Count == 0);
    }

    [PostgresFact]
    public async Task ResolveHit_OwnedFile_ByEntryIdAndFileId()
    {
        var c = fixture.Catalog;
        var service = CreateService(Catalog.Owner);

        var byEntry = await service.ResolveHit(Ref(SearchHitKind.Photo, c.Exact.EntryId), default);
        var byFile = await service.ResolveHit(Ref(SearchHitKind.Photo, c.Exact.FileId), default);

        foreach (var hit in new[] { byEntry, byFile })
        {
            hit.Id.Should().Be(c.Exact.EntryId.ToString());
            hit.FileId.Should().Be(c.Exact.FileId.ToString());
            hit.Title.Should().Be("Report");
            hit.Favorite.Should().BeTrue();
            hit.PreviewUrl.Should().Be($"http://localhost:7026/download/{c.ExactPreview512}");
        }
    }

    [PostgresFact]
    public async Task ResolveHit_FileWithoutEntry_ReturnsFileId()
    {
        var c = fixture.Catalog;

        var hit = await CreateService(Catalog.Owner).ResolveHit(Ref(SearchHitKind.Photo, c.NoEntry.FileId), default);

        hit.Id.Should().Be(c.NoEntry.FileId.ToString());
        hit.EntryId.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task ResolveHit_VideoTrackAndFile_ByKind()
    {
        var c = fixture.Catalog;
        var service = CreateService(Catalog.Owner);

        var video = await service.ResolveHit(Ref(SearchHitKind.Video, c.VideoExact.FileId), default);
        var track = await service.ResolveHit(Ref(SearchHitKind.Track, c.TrackArtist.EntryId), default);
        var file = await service.ResolveHit(Ref(SearchHitKind.File, c.DocTitle.FileId), default);

        video.Kind.Should().Be(SearchHitKind.Video);
        track.Subtitle.Should().Be("The Report Band");
        file.Subtitle.Should().Be("Quarterly Report");
    }

    [PostgresFact]
    public async Task ResolveHit_WrongKindForFile_ReturnsNotFound()
    {
        var c = fixture.Catalog;
        var service = CreateService(Catalog.Owner);

        foreach (var (kind, id) in new[]
                 {
                     (SearchHitKind.Video, c.Exact.FileId),     // фото открывают как видео
                     (SearchHitKind.File, c.Exact.EntryId),     // фото не относится к «Файлам»
                     (SearchHitKind.Photo, c.DocExact.FileId),
                 })
        {
            var act = () => service.ResolveHit(Ref(kind, id), default);

            var exception = await act.Should().ThrowAsync<RpcException>();
            exception.Which.StatusCode.Should().Be(StatusCode.NotFound, $"{kind} {id}");
        }
    }

    [PostgresFact]
    public async Task ResolveHit_Trash_ByDeletedEntryIdAndFileId_ButNotWhenLiveEntryExists()
    {
        var c = fixture.Catalog;
        var service = CreateService(Catalog.Owner);

        var byEntry = await service.ResolveHit(Ref(SearchHitKind.Trash, c.TrashPhoto.EntryId), default);
        var byFile = await service.ResolveHit(Ref(SearchHitKind.Trash, c.TrashPhoto.FileId), default);
        var restored = () => service.ResolveHit(Ref(SearchHitKind.Trash, c.RestoredDeletedEntryId), default);
        var latest = await service.ResolveHit(Ref(SearchHitKind.Trash, c.TrashTwoEntriesMatching.FileId), default);

        byEntry.Id.Should().Be(c.TrashPhoto.EntryId.ToString());
        byFile.Id.Should().Be(c.TrashPhoto.EntryId.ToString());
        byEntry.Kind.Should().Be(SearchHitKind.Trash);
        (await restored.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
        latest.Id.Should().Be(c.TrashTwoEntriesMatching.EntryId.ToString(), "для файла в корзине берётся последняя удалённая запись");
    }

    [PostgresFact]
    public async Task ResolveHit_AlbumPlaylistFolderAndSystemFolder()
    {
        var c = fixture.Catalog;
        var service = CreateService(Catalog.Owner);

        var album = await service.ResolveHit(Ref(SearchHitKind.Album, c.AlbumExact), default);
        var playlist = await service.ResolveHit(Ref(SearchHitKind.Playlist, c.PlaylistPrefix), default);
        var folder = await service.ResolveHit(Ref(SearchHitKind.Folder, c.DirPrefix), default);
        var dynamicFolder = await service.ResolveHit(Ref(SearchHitKind.DynamicFolder, c.DynamicExact), default);
        var system = await service.ResolveHit(new SearchHitReference { Kind = SearchHitKind.DynamicFolder, Id = SystemDynamicFolders.KeyLarge }, default);

        album.Title.Should().Be("Report");
        playlist.Title.Should().Be("Reports mix");
        folder.Title.Should().Be("Reports");
        folder.Subtitle.Should().Be("Папка");
        dynamicFolder.Subtitle.Should().Be("Умная папка");
        system.Id.Should().Be(SystemDynamicFolders.KeyLarge);
        system.Subtitle.Should().Be("Системная умная папка");
    }

    [PostgresFact]
    public async Task ResolveHit_ForeignAlbumFolderAndWrongKind_ReturnsNotFound()
    {
        var c = fixture.Catalog;
        var service = CreateService(Catalog.Owner);

        foreach (var (kind, id) in new[]
                 {
                     (SearchHitKind.Album, c.ForeignAlbum),
                     (SearchHitKind.Folder, c.ForeignDir),
                     (SearchHitKind.Folder, c.DynamicExact),   // умная папка — другой Kind
                     (SearchHitKind.Playlist, c.AlbumExact),
                 })
        {
            var act = () => service.ResolveHit(Ref(kind, id), default);

            var exception = await act.Should().ThrowAsync<RpcException>();
            exception.Which.StatusCode.Should().Be(StatusCode.NotFound, $"{kind} {id}");
        }
    }

    [PostgresFact]
    public async Task ResolveHit_Shared_ByGrantIdAndByTargetId_OnlyForRecipient()
    {
        var c = fixture.Catalog;
        var recipient = CreateService(Catalog.Owner);

        var fileByGrant = await recipient.ResolveHit(Ref(SearchHitKind.SharedFile, c.FileGrant), default);
        var fileByTarget = await recipient.ResolveHit(Ref(SearchHitKind.SharedFile, c.SharedFileId), default);
        var folder = await recipient.ResolveHit(Ref(SearchHitKind.SharedFolder, c.DirGrant), default);
        var playlist = await recipient.ResolveHit(Ref(SearchHitKind.SharedPlaylist, c.SharedPlaylistId), default);

        fileByGrant.Id.Should().Be(c.FileGrant.ToString());
        fileByTarget.Id.Should().Be(c.FileGrant.ToString());
        folder.FileId.Should().Be(c.SharedDirId.ToString());
        playlist.Id.Should().Be(c.PlaylistGrant.ToString());
        foreach (var (kind, id) in new[] { (SearchHitKind.SharedFile, c.FileGrantForOtherRecipient), (SearchHitKind.SharedFile, c.UnreadySharedGrant) })
        {
            var act = () => recipient.ResolveHit(Ref(kind, id), default);

            var exception = await act.Should().ThrowAsync<RpcException>();
            exception.Which.StatusCode.Should().Be(StatusCode.NotFound, $"{kind} {id}");
        }
    }

    [PostgresFact]
    public async Task ResolveHit_InvalidIds_ReturnNotFoundOrInvalidArgument()
    {
        var service = CreateService(Catalog.Owner);

        var notGuid = () => service.ResolveHit(new SearchHitReference { Kind = SearchHitKind.Photo, Id = "not-a-guid" }, default);
        var unknownKind = () => service.ResolveHit(new SearchHitReference { Kind = SearchHitKind.Unspecified, Id = Guid.NewGuid().ToString() }, default);
        var empty = () => service.ResolveHit(new SearchHitReference { Kind = SearchHitKind.Photo, Id = " " }, default);

        (await notGuid.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
        (await unknownKind.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        (await empty.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [PostgresFact]
    public async Task Search_BulkCatalog_TraversesEveryRowOnceInKeysetOrder()
    {
        var bulk = fixture.Catalog.Bulk;
        var service = CreateService(Catalog.BulkOwner);

        var traversed = await Traverse(service, SearchSection.Photos, "bulk", 50);

        traversed.Should().HaveCount(Catalog.BulkCount);
        traversed.Select(x => x.Id).Should().OnlyHaveUniqueItems();
        traversed.Select(x => x.Id).Should().Equal(bulk.OrderByDescending(x => x.SortAt)
            .ThenByDescending(x => x.EntryId.ToString(), StringComparer.Ordinal).Select(x => x.EntryId.ToString()).ToList());
    }

    [PostgresFact]
    public async Task Search_WideQuery_ReadsRowsProportionalToLimitNotToCatalog()
    {
        var counter = new RowCounter();
        var service = CreateService(Catalog.BulkOwner, counter);

        var result = await Find(service, SearchSection.Photos, "bulk", limit: 20);

        result.Hits.Should().HaveCount(20);
        result.HasMore.Should().BeTrue();
        counter.Rows.Should().BeLessThan(150, $"каталог из {Catalog.BulkCount} совпадений не должен целиком попадать в приложение");
        counter.Commands.Should().BeLessThanOrEqualTo(10, "одна секция — без загрузки остальных разделов");
    }

    [PostgresFact]
    public async Task Search_NarrowQueryInHugeCatalog_ReadsFewRows()
    {
        var counter = new RowCounter();
        var service = CreateService(Catalog.BulkOwner, counter);

        var result = await Find(service, SearchSection.Photos, "quokka", limit: 20);

        result.Hits.Should().ContainSingle().Which.Title.Should().Be("quokka-unique.jpg");
        counter.Rows.Should().BeLessThan(40);
    }

    [PostgresFact]
    public async Task ResolveHit_InHugeCatalog_ReadsFewRowsAndCommands()
    {
        var target = fixture.Catalog.Bulk[Catalog.BulkCount / 2];
        var counter = new RowCounter();
        var service = CreateService(Catalog.BulkOwner, counter);

        var hit = await service.ResolveHit(Ref(SearchHitKind.Photo, target.EntryId), default);

        hit.FileId.Should().Be(target.FileId.ToString());
        counter.Rows.Should().BeLessThan(40, "разрешение одного ID не должно материализовать каталог");
        counter.Commands.Should().BeLessThanOrEqualTo(10);
    }

    private Task<SearchSectionResult> Find(SearchSection section, string query, int limit = 50, string cursor = "")
        => Find(CreateService(Catalog.Owner), section, query, limit, cursor);

    private static async Task<SearchSectionResult> Find(UnifiedSearchService service, SearchSection section, string query, int limit = 50, string cursor = "")
    {
        var response = await service.Search(new SearchRequest
        {
            Query = query,
            Pages = { new SearchSectionPage { Section = section, Limit = limit, Cursor = cursor } }
        }, default);
        return response.Sections.Should().ContainSingle().Which;
    }

    private static async Task<List<SearchHit>> Traverse(UnifiedSearchService service, SearchSection section, string query, int pageSize)
    {
        var hits = new List<SearchHit>();
        var cursor = "";
        for (var guard = 0; guard < 10_000; guard++)
        {
            var page = await Find(service, section, query, pageSize, cursor);
            hits.AddRange(page.Hits);
            if (!page.HasMore)
            {
                page.NextCursor.Should().BeEmpty();
                return hits;
            }
            page.Hits.Should().HaveCount(pageSize);
            page.NextCursor.Should().NotBeEmpty();
            cursor = page.NextCursor;
        }
        throw new InvalidOperationException("Курсор не завершился.");
    }

    private static string[] Ids(params Guid[] ids) => ids.Select(x => x.ToString()).ToArray();

    private static SearchHitReference Ref(SearchHitKind kind, Guid id) => new() { Kind = kind, Id = id.ToString() };

    private UnifiedSearchService CreateService(long userId, params IInterceptor[] interceptors) => new(
        fixture.Database!.CreateContext(interceptors),
        UserContextFactory.Create(userId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 },
        TestConfiguration.Empty());

    /// <summary>Считает SQL-команды и строки, прочитанные из БД, — мера объёма работы приложения.</summary>
    private sealed class RowCounter : DbCommandInterceptor
    {
        public int Commands { get; private set; }

        public long Rows { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            Rows += eventData.ReadCount;
            return result;
        }
    }
}

/// <summary>Каталог для поиска: сидится один раз на класс тестов, тесты только читают.</summary>
public sealed class SearchCatalogFixture : IAsyncLifetime
{
    public PostgresFilesDatabase? Database { get; private set; }

    public Catalog Catalog { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (PostgresFilesDatabase.SkipReason is not null)
            return;

        Database = await PostgresFilesDatabase.CreateAsync();
        await using var context = Database.CreateContext();
        Catalog = await Catalog.Seed(context);
    }

    public async Task DisposeAsync()
    {
        if (Database is not null)
            await Database.DisposeAsync();
    }
}

public sealed record SeededFile(Guid FileId, Guid EntryId, DateTime CreatedAt, DateTime? DeletedAt = null)
{
    public DateTime SortAt => DeletedAt ?? CreatedAt;
}

/// <summary>
/// Содержимое каталога: владелец <see cref="Owner"/> ищет «report». Данные <see cref="Other"/> — чужие;
/// <see cref="BulkOwner"/> — большой каталог для проверки объёма чтения.
/// </summary>
public sealed class Catalog
{
    public const long Owner = 100;
    public const long Other = 200;
    public const long ThirdUser = 300;
    public const long BulkOwner = 500;
    public const int BulkCount = 2000;

    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public SeededFile Exact = null!, TagExact = null!, PrefixNew = null!, PrefixOld = null!, NoEntry = null!, AliasSub = null!, Sub = null!, Typo = null!;
    public SeededFile PhotoWithForeignAlias = null!;
    public Guid ExactPreview512;
    public SeededFile VideoExact = null!, VideoSub = null!;
    public SeededFile TrackExact = null!, TrackAlbum = null!, TrackArtist = null!, TrackTitle = null!, TrackWithDocumentTitle = null!;
    public SeededFile DocExact = null!, OtherPrefix = null!, DocAuthor = null!, DocTitle = null!, OtherWithDocumentTitle = null!;
    public SeededFile TrashPhoto = null!, TrashDoc = null!, TrashAudioArtist = null!, TrashTwoEntriesMatching = null!;
    public Guid RestoredDeletedEntryId;
    public Guid AlbumExact, AlbumPrefix, AlbumDescription, ForeignAlbum;
    public Guid PlaylistPrefix, PlaylistDescription;
    public Guid DynamicExact, DirPrefix, DirSub, DirRecent, ForeignDir;
    public Guid FileGrant, DirGrant, PlaylistGrant, SharedFileId, SharedDirId, SharedPlaylistId, FileGrantForOtherRecipient, UnreadySharedGrant;
    public List<SeededFile> Bulk = [];
    public SeededFile BulkNeedle = null!;
    public string[] HiddenTitles = [];
    public Guid[] HiddenFileIds = [];

    public static async Task<Catalog> Seed(FilesContext db)
    {
        var c = new Catalog();
        var hiddenTitles = new List<string>();
        var hiddenFileIds = new List<Guid>();

        UploadFile NewFile(string? filename, MediaKind kind, DateTime createdAt, long[]? uploaders = null, bool ready = true,
            UploadFileType type = UploadFileType.CloudFile, long size = 1234) => new()
        {
            Id = Guid.NewGuid(),
            Uploaders = (uploaders ?? [Owner]).ToList(),
            Type = type,
            MediaKind = kind,
            Filename = filename,
            StorageProfileId = "universal-v1",
            Size = size,
            CreatedAt = createdAt,
            UploadedAt = ready ? createdAt : null,
            Etag = ready ? "etag" : null,
        };

        CloudFileEntry NewEntry(UploadFile file, string name, long owner = Owner, bool deleted = false, DateTime? deletedAt = null) => new()
        {
            Id = Guid.NewGuid(),
            OwnerId = owner,
            DirectoryId = Guid.NewGuid(),
            FileId = file.Id,
            Name = name,
            CreatedAt = file.CreatedAt,
            IsDeleted = deleted,
            DeletedAt = deleted ? deletedAt : null,
            PurgeAt = deleted ? deletedAt?.AddDays(30) : null,
        };

        var files = new List<UploadFile>();
        var entries = new List<CloudFileEntry>();
        var aliases = new List<FileSearchAlias>();
        var tags = new List<FileTag>();
        var metadata = new List<FileMetadata>();
        var favorites = new List<FavoriteFile>();
        var previews = new List<FilePreview>();

        // Живой файл с записью облака владельца.
        SeededFile Live(string name, MediaKind kind, int day, string? filename = null, Action<UploadFile>? tune = null)
        {
            var file = NewFile(filename ?? name, kind, At(day));
            tune?.Invoke(file);
            var entry = NewEntry(file, name);
            files.Add(file);
            entries.Add(entry);
            return new SeededFile(file.Id, entry.Id, file.CreatedAt);
        }

        FileMetadata Meta(SeededFile file, Action<FileMetadata> set)
        {
            var meta = new FileMetadata { FileId = file.FileId, CreatedAt = Base };
            set(meta);
            metadata.Add(meta);
            return meta;
        }

        // ===== Фото (раздел Photos): ранги 4/3/2/1 + граничные случаи =====
        c.Exact = Live("Report", MediaKind.Photo, 10);
        c.TagExact = Live("img-001.jpg", MediaKind.Photo, 20);
        tags.Add(new FileTag { OwnerId = Owner, FileId = c.TagExact.FileId, Value = "report", NormalizedValue = "report", CreatedAt = Base });
        c.PrefixNew = Live("report-final.jpg", MediaKind.Photo, 15);
        c.PrefixOld = Live("report-draft.jpg", MediaKind.Photo, 5);
        c.Sub = Live("annual report.jpg", MediaKind.Photo, 12);
        c.AliasSub = Live("img-002.jpg", MediaKind.Photo, 30);
        aliases.Add(new FileSearchAlias { OwnerId = Owner, FileId = c.AliasSub.FileId, Value = "My report", NormalizedValue = "my report", UpdatedAt = Base });
        c.Typo = Live("repport.jpg", MediaKind.Photo, 40);

        var noEntryFile = NewFile("report-noentry.jpg", MediaKind.Photo, At(14));
        files.Add(noEntryFile);
        c.NoEntry = new SeededFile(noEntryFile.Id, Guid.Empty, noEntryFile.CreatedAt);

        // Избранное: моё и чужое (на другом файле, чужое не должно влиять).
        favorites.Add(new FavoriteFile { Id = Guid.NewGuid(), OwnerId = Owner, FileId = c.Exact.FileId, CreatedAt = Base });
        favorites.Add(new FavoriteFile { Id = Guid.NewGuid(), OwnerId = Other, FileId = c.TagExact.FileId, CreatedAt = Base });

        // Превью оригинала: 128 и 512 — в карточке берётся 512.
        foreach (var width in new[] { 128, 512 })
        {
            var blob = NewFile(null, MediaKind.Photo, At(10));
            files.Add(blob);
            previews.Add(new FilePreview { Id = Guid.NewGuid(), OriginalFileId = c.Exact.FileId, PreviewFileId = blob.Id, TargetWidth = width, CreatedAt = Base });
            if (width == 512)
                c.ExactPreview512 = blob.Id;
        }

        // ----- Скрытое от владельца -----
        // 1) Файл другого пользователя с совпадающим именем.
        var foreignFile = NewFile("report-foreign.jpg", MediaKind.Photo, At(11), [Other]);
        files.Add(foreignFile);
        entries.Add(NewEntry(foreignFile, "report-foreign.jpg", Other));
        // 2) Мой файл, найденный только по чужому алиасу (дедуп: у файла два владельца).
        var shared = NewFile("holiday.jpg", MediaKind.Photo, At(50), [Owner, Other]);
        files.Add(shared);
        var sharedEntry = NewEntry(shared, "holiday.jpg");
        entries.Add(sharedEntry);
        aliases.Add(new FileSearchAlias { OwnerId = Other, FileId = shared.Id, Value = "report", NormalizedValue = "report", UpdatedAt = Base });
        tags.Add(new FileTag { OwnerId = Other, FileId = shared.Id, Value = "report", NormalizedValue = "report", CreatedAt = Base });
        c.PhotoWithForeignAlias = new SeededFile(shared.Id, sharedEntry.Id, shared.CreatedAt);
        // 3) Превью-блоб с совпадающим Filename.
        var previewByName = NewFile("report-preview.jpg", MediaKind.Photo, At(13));
        files.Add(previewByName);
        entries.Add(NewEntry(previewByName, "report-preview.jpg"));
        previews.Add(new FilePreview { Id = Guid.NewGuid(), OriginalFileId = shared.Id, PreviewFileId = previewByName.Id, TargetWidth = 512, CreatedAt = Base });
        // 4) Не дозагруженный файл и аватар.
        var pending = NewFile("report-pending.jpg", MediaKind.Photo, At(16), ready: false);
        files.Add(pending);
        entries.Add(NewEntry(pending, "report-pending.jpg"));
        var avatar = NewFile("report-avatar.jpg", MediaKind.Photo, At(17), type: UploadFileType.UserAvatar);
        files.Add(avatar);
        // 5) Имя блоба совпадает, а запись облака названа иначе — поиск идёт по имени записи.
        var renamed = Live("IMG_0001.jpg", MediaKind.Photo, 18, filename: "report-orig.jpg");
        // 6) Живая запись названа иначе, чем удалённая запись того же файла — файл не в корзине и не находится.
        var restoredFile = NewFile("report-restored.jpg", MediaKind.Photo, At(19));
        files.Add(restoredFile);
        entries.Add(NewEntry(restoredFile, "restored.jpg"));
        var restoredDeleted = NewEntry(restoredFile, "report-restored.jpg", deleted: true, deletedAt: At(75));
        entries.Add(restoredDeleted);
        c.RestoredDeletedEntryId = restoredDeleted.Id;

        // ===== Видео =====
        c.VideoExact = Live("report.mp4", MediaKind.Video, 10);
        c.VideoSub = Live("q3 report.mov", MediaKind.Video, 12);

        // ===== Треки =====
        c.TrackExact = Live("Report", MediaKind.Audio, 10);
        c.TrackAlbum = Live("track-a.mp3", MediaKind.Audio, 11);
        Meta(c.TrackAlbum, m => m.AudioAlbum = "Reports");
        c.TrackArtist = Live("track-b.mp3", MediaKind.Audio, 13);
        Meta(c.TrackArtist, m => m.AudioArtist = "The Report Band");
        c.TrackTitle = Live("track-c.mp3", MediaKind.Audio, 12);
        Meta(c.TrackTitle, m => m.AudioTitle = "The Report");
        c.TrackWithDocumentTitle = Live("track-d.mp3", MediaKind.Audio, 14);
        Meta(c.TrackWithDocumentTitle, m => m.DocumentTitle = "report");

        // ===== Файлы =====
        c.DocExact = Live("report.docx", MediaKind.Document, 10);
        c.OtherPrefix = Live("report-data.bin", MediaKind.Other, 9);
        c.DocAuthor = Live("memo.pdf", MediaKind.Document, 8);
        Meta(c.DocAuthor, m => m.DocumentAuthor = "Report Team");
        c.DocTitle = Live("notes.pdf", MediaKind.Document, 7);
        Meta(c.DocTitle, m => m.DocumentTitle = "Quarterly Report");
        c.OtherWithDocumentTitle = Live("plain.bin", MediaKind.Other, 6);
        Meta(c.OtherWithDocumentTitle, m => m.DocumentTitle = "report");

        // ===== Корзина =====
        SeededFile Trashed(string name, MediaKind kind, int createdDay, int deletedDay, Action<UploadFile>? tune = null)
        {
            var file = NewFile(name, kind, At(createdDay));
            tune?.Invoke(file);
            var entry = NewEntry(file, name, deleted: true, deletedAt: At(deletedDay));
            files.Add(file);
            entries.Add(entry);
            return new SeededFile(file.Id, entry.Id, file.CreatedAt, entry.DeletedAt);
        }

        c.TrashPhoto = Trashed("report-old.jpg", MediaKind.Photo, 1, 60);
        c.TrashDoc = Trashed("report-old.pdf", MediaKind.Document, 2, 61);
        c.TrashAudioArtist = Trashed("old-track.mp3", MediaKind.Audio, 3, 62);
        Meta(c.TrashAudioArtist, m => m.AudioArtist = "The Report Band");
        // Два удалённых записи у одного файла: подходит только последняя по DeletedAt.
        var twoMatching = NewFile("two-a", MediaKind.Photo, At(4));
        files.Add(twoMatching);
        entries.Add(NewEntry(twoMatching, "old-a.jpg", deleted: true, deletedAt: At(50)));
        var twoMatchingLatest = NewEntry(twoMatching, "report-b.jpg", deleted: true, deletedAt: At(80));
        entries.Add(twoMatchingLatest);
        c.TrashTwoEntriesMatching = new SeededFile(twoMatching.Id, twoMatchingLatest.Id, twoMatching.CreatedAt, twoMatchingLatest.DeletedAt);
        var twoNotMatching = NewFile("two-b", MediaKind.Photo, At(5));
        files.Add(twoNotMatching);
        entries.Add(NewEntry(twoNotMatching, "report-c.jpg", deleted: true, deletedAt: At(40)));
        entries.Add(NewEntry(twoNotMatching, "other.jpg", deleted: true, deletedAt: At(90)));

        // ===== Альбомы, плейлисты, папки =====
        var albums = new List<Album>();
        Guid NewAlbum(string name, string? description, int day, long owner = Owner)
        {
            var album = new Album { Id = Guid.NewGuid(), OwnerId = owner, Name = name, Description = description, CreatedAt = At(day), UpdatedAt = At(day) };
            albums.Add(album);
            return album.Id;
        }

        c.AlbumExact = NewAlbum("Report", null, 5);
        c.AlbumPrefix = NewAlbum("Reports 2024", null, 7);
        c.AlbumDescription = NewAlbum("Misc", "weekly report", 9);
        c.ForeignAlbum = NewAlbum("Report", null, 6, Other);

        var playlists = new List<MusicPlaylist>();
        Guid NewPlaylist(string name, string? description, int day, long owner = Owner)
        {
            var playlist = new MusicPlaylist { Id = Guid.NewGuid(), OwnerId = owner, Name = name, Description = description, CreatedAt = At(day), UpdatedAt = At(day) };
            playlists.Add(playlist);
            return playlist.Id;
        }

        c.PlaylistPrefix = NewPlaylist("Reports mix", null, 6);
        c.PlaylistDescription = NewPlaylist("Evening", "weekly report soundtrack", 8);
        NewPlaylist("report", null, 6, Other);

        var directories = new List<CloudDirectory>();
        Guid NewDir(string name, int day, long owner = Owner)
        {
            var dir = new CloudDirectory { Id = Guid.NewGuid(), OwnerId = owner, Name = name, CreatedAt = At(day), UpdatedAt = At(day) };
            directories.Add(dir);
            return dir.Id;
        }

        c.DirPrefix = NewDir("Reports", 3);
        c.DirSub = NewDir("my report folder", 8);
        c.DirRecent = NewDir("Недавние отчёты", 4);
        c.ForeignDir = NewDir("report", 3, Other);
        c.SharedDirId = NewDir("Report share", 3, Other);

        var dynamicFolders = new List<DynamicFolder>();
        Guid NewDynamic(string name, int day, long owner = Owner)
        {
            var folder = new DynamicFolder { Id = Guid.NewGuid(), OwnerId = owner, Name = name, CreatedAt = At(day), UpdatedAt = At(day) };
            dynamicFolders.Add(folder);
            return folder.Id;
        }

        c.DynamicExact = NewDynamic("report", 9);
        NewDynamic("report", 9, Other);

        // ===== Шаринг: гранты, выданные Other получателю Owner =====
        var sharedFile = NewFile("report-shared.pdf", MediaKind.Document, At(2), [Other]);
        var unreadyShared = NewFile("report-unready.pdf", MediaKind.Document, At(2), [Other], ready: false);
        var forThird = NewFile("report-for-300.pdf", MediaKind.Document, At(2), [Other]);
        files.AddRange([sharedFile, unreadyShared, forThird]);
        c.SharedFileId = sharedFile.Id;
        var sharedPlaylist = new MusicPlaylist { Id = Guid.NewGuid(), OwnerId = Other, Name = "report mix shared", CreatedAt = At(2), UpdatedAt = At(2) };
        playlists.Add(sharedPlaylist);
        c.SharedPlaylistId = sharedPlaylist.Id;

        c.FileGrant = Guid.NewGuid();
        c.DirGrant = Guid.NewGuid();
        c.PlaylistGrant = Guid.NewGuid();
        c.FileGrantForOtherRecipient = Guid.NewGuid();
        c.UnreadySharedGrant = Guid.NewGuid();
        var fileGrants = new List<FileGrant>
        {
            new() { Id = c.FileGrant, OwnerId = Other, RecipientId = Owner, FileId = sharedFile.Id, CreatedAt = At(10) },
            new() { Id = c.UnreadySharedGrant, OwnerId = Other, RecipientId = Owner, FileId = unreadyShared.Id, CreatedAt = At(11) },
            new() { Id = c.FileGrantForOtherRecipient, OwnerId = Other, RecipientId = ThirdUser, FileId = forThird.Id, CreatedAt = At(12) },
        };
        var dirGrants = new List<DirectoryGrant> { new() { Id = c.DirGrant, OwnerId = Other, RecipientId = Owner, DirectoryId = c.SharedDirId, CreatedAt = At(20) } };
        var playlistGrants = new List<MusicPlaylistGrant> { new() { Id = c.PlaylistGrant, OwnerId = Other, RecipientId = Owner, PlaylistId = sharedPlaylist.Id, CreatedAt = At(15) } };

        hiddenTitles.AddRange(["report-foreign.jpg", "report-preview.jpg", "report-pending.jpg", "report-avatar.jpg", "report-orig.jpg",
            "report-unready.pdf", "report-for-300.pdf", "report-restored.jpg", "report-c.jpg", "other.jpg", "old-a.jpg", "plain.bin", "IMG_0001.jpg"]);
        hiddenFileIds.AddRange([foreignFile.Id, previewByName.Id, pending.Id, avatar.Id, renamed.FileId, unreadyShared.Id, forThird.Id,
            restoredFile.Id, twoNotMatching.Id, c.OtherWithDocumentTitle.FileId, c.TrackWithDocumentTitle.FileId]);
        c.HiddenTitles = hiddenTitles.ToArray();
        c.HiddenFileIds = hiddenFileIds.ToArray();

        // ===== Большой каталог владельца BulkOwner: группы по 10 файлов с одинаковым временем (проверка tie-break) =====
        var rng = new Random(20);
        for (var i = 0; i < BulkCount; i++)
        {
            var createdAt = Base.AddMinutes(i / 10);
            var file = NewFile($"bulk-{i:D4}.jpg", MediaKind.Photo, createdAt, [BulkOwner], size: rng.Next(1000, 9000));
            var entry = NewEntry(file, $"bulk-{i:D4}.jpg", BulkOwner);
            files.Add(file);
            entries.Add(entry);
            c.Bulk.Add(new SeededFile(file.Id, entry.Id, createdAt));
        }

        var needle = NewFile("quokka-unique.jpg", MediaKind.Photo, At(1), [BulkOwner]);
        var needleEntry = NewEntry(needle, "quokka-unique.jpg", BulkOwner);
        files.Add(needle);
        entries.Add(needleEntry);
        c.BulkNeedle = new SeededFile(needle.Id, needleEntry.Id, needle.CreatedAt);

        db.UploadedFiles.AddRange(files);
        db.Albums.AddRange(albums);
        db.MusicPlaylists.AddRange(playlists);
        db.CloudDirectories.AddRange(directories);
        db.DynamicFolders.AddRange(dynamicFolders);
        await db.SaveChangesAsync();

        db.CloudFileEntries.AddRange(entries);
        db.FileSearchAliases.AddRange(aliases);
        db.FileTags.AddRange(tags);
        db.FileMetadata.AddRange(metadata);
        db.FavoriteFiles.AddRange(favorites);
        db.FilePreviews.AddRange(previews);
        db.FileGrants.AddRange(fileGrants);
        db.DirectoryGrants.AddRange(dirGrants);
        db.MusicPlaylistGrants.AddRange(playlistGrants);
        await db.SaveChangesAsync();
        return c;
    }

    private static DateTime At(int day) => Base.AddDays(day);
}
