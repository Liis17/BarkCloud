using BarkCloud.Files.Domain;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Proto.Users;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Tests.Services;

public sealed class StaleAvatarReleaserTests : IDisposable
{
    private const long UserId = 42;
    private const long OtherUserId = 7;
    private const string BaseUrl = "https://cloud.example/web/download/";

    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Cutoff = Now - StaleAvatarReleaser.Grace;
    private static readonly DateTime Old = Cutoff.AddHours(-1);

    private readonly SqliteFilesContext _db = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _users = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ReplacedAvatar_ReleasesPreviousFileAndPreview_KeepsCurrent()
    {
        var oldMain = Guid.NewGuid();
        var oldPreview = Guid.NewGuid();
        var currentMain = Guid.NewGuid();
        var currentPreview = Guid.NewGuid();
        await SeedAsync(Avatar(oldMain, Old, UserId), Avatar(oldPreview, Old, UserId),
            Avatar(currentMain, Old, UserId), Avatar(currentPreview, Old, UserId));
        SetupUsers(Profile(UserId, currentMain, currentPreview));

        var released = await CreateSut().ReleaseAsync(Cutoff, default);

        released.Should().Be(2);
        Uploaders(oldMain).Should().BeEmpty();
        Uploaders(oldPreview).Should().BeEmpty();
        Uploaders(currentMain).Should().Equal(UserId);
        Uploaders(currentPreview).Should().Equal(UserId);
    }

    [Fact]
    public async Task RemovedAvatar_ReleasesAllOldAvatarFiles()
    {
        var main = Guid.NewGuid();
        var preview = Guid.NewGuid();
        await SeedAsync(Avatar(main, Old, UserId), Avatar(preview, Old, UserId));
        SetupUsers(new User { Id = UserId });

        await CreateSut().ReleaseAsync(Cutoff, default);

        Uploaders(main).Should().BeEmpty();
        Uploaders(preview).Should().BeEmpty();
    }

    [Fact]
    public async Task UploadedButNeverAssigned_IsReleasedOnlyAfterGrace()
    {
        // Сбой между загрузкой в Files и назначением в Users: файл есть, в профиле его нет.
        var stale = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        await SeedAsync(Avatar(stale, Old, UserId), Avatar(fresh, Cutoff.AddMinutes(1), UserId));
        SetupUsers(new User { Id = UserId });

        await CreateSut().ReleaseAsync(Cutoff, default);

        Uploaders(stale).Should().BeEmpty();
        Uploaders(fresh).Should().Equal(UserId);
    }

    [Fact]
    public async Task RepeatedRun_IsIdempotent()
    {
        var oldMain = Guid.NewGuid();
        var current = Guid.NewGuid();
        await SeedAsync(Avatar(oldMain, Old, UserId), Avatar(current, Old, UserId));
        SetupUsers(Profile(UserId, current, null));

        await CreateSut().ReleaseAsync(Cutoff, default);
        var second = await CreateSut().ReleaseAsync(Cutoff, default);

        second.Should().Be(0);
        Uploaders(oldMain).Should().BeEmpty();
        Uploaders(current).Should().Equal(UserId);
    }

    [Fact]
    public async Task FileWithAnotherOwner_KeepsOtherOwner()
    {
        var shared = Guid.NewGuid();
        await SeedAsync(Avatar(shared, Old, UserId, OtherUserId));
        SetupUsers(new User { Id = UserId }, Profile(OtherUserId, shared, null));

        await CreateSut().ReleaseAsync(Cutoff, default);

        Uploaders(shared).Should().Equal(OtherUserId);
    }

    [Fact]
    public async Task UsersFailure_ReleasesNothing()
    {
        var oldMain = Guid.NewGuid();
        await SeedAsync(Avatar(oldMain, Old, UserId));
        _users.Setup(x => x.ListByIdsAsync(It.IsAny<ListByIdsRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "down")));

        var act = () => CreateSut().ReleaseAsync(Cutoff, default);

        await act.Should().ThrowAsync<RpcException>();
        Uploaders(oldMain).Should().Equal(UserId);
    }

    [Fact]
    public async Task UserMissingInUsers_IsSkipped()
    {
        var file = Guid.NewGuid();
        await SeedAsync(Avatar(file, Old, UserId));
        SetupUsers();

        await CreateSut().ReleaseAsync(Cutoff, default);

        Uploaders(file).Should().Equal(UserId);
    }

    [Fact]
    public async Task CurrentAvatarWithDifferentBaseUrl_IsKept()
    {
        var current = Guid.NewGuid();
        await SeedAsync(Avatar(current, Old, UserId));
        SetupUsers(new User { Id = UserId, ProfilePicture = $"http://localhost:7006/download/{current}" });

        await CreateSut().ReleaseAsync(Cutoff, default);

        Uploaders(current).Should().Equal(UserId);
    }

    [Fact]
    public async Task UnparsableProfileUrl_SkipsUser()
    {
        var file = Guid.NewGuid();
        await SeedAsync(Avatar(file, Old, UserId));
        SetupUsers(new User { Id = UserId, ProfilePicture = "https://legacy.example/avatar.jpg" });

        await CreateSut().ReleaseAsync(Cutoff, default);

        Uploaders(file).Should().Equal(UserId);
    }

    [Fact]
    public async Task AvatarReferencedByCloudEntry_IsKept()
    {
        var file = Guid.NewGuid();
        await SeedAsync(Avatar(file, Old, UserId));
        _db.Context.CloudFileEntries.Add(new CloudFileEntry
        {
            Id = Guid.NewGuid(), OwnerId = UserId, FileId = file, Name = "avatar.jpg", CreatedAt = Now
        });
        await _db.Context.SaveChangesAsync();
        SetupUsers(new User { Id = UserId });

        await CreateSut().ReleaseAsync(Cutoff, default);

        Uploaders(file).Should().Equal(UserId);
    }

    [Fact]
    public async Task CloudFiles_AreNotTouched()
    {
        var cloud = Guid.NewGuid();
        var file = Avatar(cloud, Old, UserId);
        file.Type = UploadFileType.CloudFile;
        await SeedAsync(file);

        var released = await CreateSut().ReleaseAsync(Cutoff, default);

        released.Should().Be(0);
        Uploaders(cloud).Should().Equal(UserId);
        _users.VerifyNoOtherCalls();
    }

    private StaleAvatarReleaser CreateSut() =>
        new(_db.Context, _users.Object, NullLogger<StaleAvatarReleaser>.Instance);

    private async Task SeedAsync(params UploadFile[] files)
    {
        _db.Context.UploadedFiles.AddRange(files);
        await _db.Context.SaveChangesAsync();
    }

    private List<long> Uploaders(Guid fileId)
    {
        _db.Context.ChangeTracker.Clear();
        return _db.Context.UploadedFiles.Single(x => x.Id == fileId).Uploaders;
    }

    private void SetupUsers(params User[] users) =>
        _users.Setup(x => x.ListByIdsAsync(It.IsAny<ListByIdsRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns((ListByIdsRequest request, Metadata _, DateTime? _, CancellationToken _) =>
            {
                var response = new ListByIdsResponse();
                response.Users.AddRange(users.Where(u => request.Ids.Contains(u.Id)));
                return new AsyncUnaryCall<ListByIdsResponse>(Task.FromResult(response),
                    Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
            });

    private static User Profile(long userId, Guid main, Guid? preview) => new()
    {
        Id = userId,
        ProfilePicture = BaseUrl + main,
        ProfilePicturePreview = preview is { } p ? BaseUrl + p : string.Empty
    };

    private static UploadFile Avatar(Guid id, DateTime uploadedAt, params long[] uploaders) => new()
    {
        Id = id,
        Uploaders = uploaders.ToList(),
        Type = UploadFileType.UserAvatar,
        MediaKind = MediaKind.Photo,
        CreatedAt = uploadedAt,
        UploadedAt = uploadedAt,
        Etag = "etag",
        StorageProfileId = "avatars",
        Filename = $"{id}.jpg"
    };
}

public sealed class StaleAvatarReleaserPostgresTests
{
    [PostgresFact]
    public async Task ReplacedAvatar_ReleasesOnlyStaleOwnerOnPostgres()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var oldMain = Guid.NewGuid();
        var current = Guid.NewGuid();
        var uploadedAt = DateTime.UtcNow - StaleAvatarReleaser.Grace - TimeSpan.FromHours(1);
        await using (var seed = database.CreateContext())
        {
            seed.UploadedFiles.AddRange(Avatar(oldMain, uploadedAt, 42, 7), Avatar(current, uploadedAt, 42));
            await seed.SaveChangesAsync();
        }

        var users = new Mock<UsersServerApi.UsersServerApiClient>();
        var response = new ListByIdsResponse();
        response.Users.Add(new User { Id = 42, ProfilePicture = $"https://cloud.example/web/download/{current}" });
        users.Setup(x => x.ListByIdsAsync(It.IsAny<ListByIdsRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(new AsyncUnaryCall<ListByIdsResponse>(Task.FromResult(response),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { }));

        await using (var context = database.CreateContext())
        {
            var released = await new StaleAvatarReleaser(context, users.Object, NullLogger<StaleAvatarReleaser>.Instance)
                .ReleaseAsync(DateTime.UtcNow - StaleAvatarReleaser.Grace, default);
            released.Should().Be(1);
        }

        await using var verify = database.CreateContext();
        // Пользователь 7 не вернулся из Users — его владение не трогаем.
        verify.UploadedFiles.Single(x => x.Id == oldMain).Uploaders.Should().Equal(7);
        verify.UploadedFiles.Single(x => x.Id == current).Uploaders.Should().Equal(42);
    }

    private static UploadFile Avatar(Guid id, DateTime uploadedAt, params long[] uploaders) => new()
    {
        Id = id,
        Uploaders = uploaders.ToList(),
        Type = UploadFileType.UserAvatar,
        MediaKind = MediaKind.Photo,
        CreatedAt = uploadedAt,
        UploadedAt = uploadedAt,
        Etag = "etag",
        StorageProfileId = "avatars",
        Filename = $"{id}.jpg"
    };
}
