using BarkCloud.Files.Domain;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;
using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Tests.Persistence;

public class UserTagsPostgresTests
{
    [PostgresFact]
    public async Task Migration_ImportsExistingTagsPerOwnerWithoutChangingAssignments()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync("20261012223000_AddCloudDirectoryVisibility");
        await using var context = database.CreateContext();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        context.UploadedFiles.AddRange(new UploadFile { Id = first }, new UploadFile { Id = second });
        context.FileTags.AddRange(
            new FileTag { OwnerId = 42, FileId = first, Value = "Личное", NormalizedValue = "личное" },
            new FileTag { OwnerId = 42, FileId = second, Value = "личное", NormalizedValue = "личное" },
            new FileTag { OwnerId = 99, FileId = first, Value = "Личное", NormalizedValue = "личное" });
        await context.SaveChangesAsync();
        await context.Database.MigrateAsync();
        var tags = await context.UserTags.AsNoTracking().ToListAsync();
        tags.Should().HaveCount(2);
        tags.Select(x => x.OwnerId).Should().BeEquivalentTo([42L, 99L]);
        tags.Should().OnlyContain(x => x.Color == "#6750A4" && x.NormalizedName == "личное");
        (await context.FileTags.CountAsync()).Should().Be(3);
    }

    [PostgresFact]
    public async Task ConcurrentCatalogCreates_AreSerializedAndReportDuplicate()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        async Task<string> Create()
        {
            await using var context = database.CreateContext();
            var service = new UnifiedSearchService(context, UserContextFactory.Create(42), new RunSettings(), TestConfiguration.Empty());
            try
            {
                await service.SaveUserTag(new SaveUserTagRequest { Name = "Личное", Color = "#123456" }, default);
                return "created";
            }
            catch (Grpc.Core.RpcException ex) { return ex.StatusCode.ToString(); }
        }
        var results = await Task.WhenAll(Create(), Create());
        results.Should().BeEquivalentTo(["created", "AlreadyExists"]);
        await using var check = database.CreateContext();
        (await check.UserTags.CountAsync()).Should().Be(1);
    }
}
