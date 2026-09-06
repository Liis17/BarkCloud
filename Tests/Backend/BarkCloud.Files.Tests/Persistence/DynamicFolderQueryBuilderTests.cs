using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class DynamicFolderQueryBuilderTests : IDisposable
{
    private const long OwnerId = 42;
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteFilesContext _db = new();

    [Fact]
    public async Task BuildQuery_LegacyDeviceField_UsesUploadDevice()
    {
        var uploadDeviceMatch = AddFile("Pixel 9", "Apple", "iPhone 17 Pro Max");
        _ = AddFile("MacBook", "Pixel", "9");
        await _db.Context.SaveChangesAsync();

        var ids = await Query(new DynamicFolderRule
        {
            Field = DfField.Device,
            Operator = DfOperator.Equals,
            Value = "pixel 9"
        });
        var startsWithIds = await Query(new DynamicFolderRule
        {
            Field = DfField.Device,
            Operator = DfOperator.StartsWith,
            Value = "PIXEL"
        });
        var endsWithIds = await Query(new DynamicFolderRule
        {
            Field = DfField.Device,
            Operator = DfOperator.EndsWith,
            Value = "9"
        });

        ids.Should().BeEquivalentTo(new[] { uploadDeviceMatch });
        startsWithIds.Should().BeEquivalentTo(new[] { uploadDeviceMatch });
        endsWithIds.Should().BeEquivalentTo(new[] { uploadDeviceMatch });
        ((int)DfField.Device).Should().Be(9);
    }

    [Fact]
    public async Task BuildQuery_MetadataDeviceField_MatchesCombinedMakeAndModel()
    {
        var metadataMatch = AddFile("MacBook", "Apple", "iPhone 17 Pro Max");
        _ = AddFile("Apple iPhone 17 Pro Max", "Sony", "Xperia 1");
        _ = AddFile("MacBook", "Apple", "iPhone 17 Pro");
        await _db.Context.SaveChangesAsync();

        var ids = await Query(new DynamicFolderRule
        {
            Field = DfField.MetadataDevice,
            Operator = DfOperator.Equals,
            Value = "Apple iPhone 17 Pro Max"
        });

        ids.Should().BeEquivalentTo(new[] { metadataMatch });
    }

    [Fact]
    public async Task BuildQuery_MetadataDeviceContains_MatchesMakeOrModelAndExcludesMissingMetadata()
    {
        var makeMatch = AddFile("Android", "Apple", "iPhone 17 Pro Max");
        var modelMatch = AddFile("Android", "Samsung", "Galaxy S25");
        _ = AddFile("Apple iPhone 17 Pro Max", null, null);
        await _db.Context.SaveChangesAsync();

        var makeIds = await Query(new DynamicFolderRule
        {
            Field = DfField.MetadataDevice,
            Operator = DfOperator.Contains,
            Value = "APPLE"
        });
        var modelIds = await Query(new DynamicFolderRule
        {
            Field = DfField.MetadataDevice,
            Operator = DfOperator.Contains,
            Value = "galaxy"
        });

        makeIds.Should().BeEquivalentTo(new[] { makeMatch });
        modelIds.Should().BeEquivalentTo(new[] { modelMatch });
    }

    private Guid AddFile(string uploadDevice, string? cameraMake, string? cameraModel)
    {
        var fileId = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = new List<long> { OwnerId },
            CreatedAt = Now,
            Type = UploadFileType.CloudFile,
            MediaKind = MediaKind.Photo,
            Filename = $"{fileId}.jpg",
            UploadDeviceName = uploadDevice,
            Size = 1
        });

        if (cameraMake is not null || cameraModel is not null)
        {
            _db.Context.FileMetadata.Add(new FileMetadata
            {
                FileId = fileId,
                CreatedAt = Now,
                CameraMake = cameraMake,
                CameraModel = cameraModel
            });
        }

        return fileId;
    }

    private Task<List<Guid>> Query(DynamicFolderRule rule)
    {
        var criteria = new DynamicFolderCriteria { Rules = new List<DynamicFolderRule> { rule } };
        return DynamicFolderQueryBuilder
            .BuildQuery(_db.Context, OwnerId, criteria, Now)
            .Select(file => file.Id)
            .ToListAsync();
    }

    public void Dispose() => _db.Dispose();
}
