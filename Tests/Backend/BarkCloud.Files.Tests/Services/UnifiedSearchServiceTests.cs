using BarkCloud.Files.Domain;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Grpc.Core;

namespace BarkCloud.Files.Tests.Services;

public class UnifiedSearchServiceTests : IDisposable
{
    private const long OwnerId = 42;
    private readonly SqliteFilesContext _db = new();

    [Fact]
    public async Task GetFileSearchMetadata_FileIsNotReady_ReturnsNotFound()
    {
        var fileId = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = new List<long> { OwnerId },
            Type = UploadFileType.CloudFile,
            Filename = "processing.pdf"
        });
        await _db.Context.SaveChangesAsync();
        var service = new UnifiedSearchService(
            _db.Context,
            UserContextFactory.Create(OwnerId),
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty());

        var act = () => service.GetFileSearchMetadata(fileId, default);

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    public void Dispose() => _db.Dispose();
}
