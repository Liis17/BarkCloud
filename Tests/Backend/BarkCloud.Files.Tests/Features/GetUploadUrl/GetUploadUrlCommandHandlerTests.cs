using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetUploadUrl;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.Tracker;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.GetUploadUrl;

public class GetUploadUrlCommandHandlerTests
{
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private GetUploadUrlCommandHandler CreateSut(long userId = 42, string? deviceName = "Pixel")
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.AddToStorage(It.IsAny<UploadFileEntity>()))
            .ReturnsAsync((UploadFileEntity f) =>
            {
                f.Id = fileId;
                return f;
            });

        return new GetUploadUrlCommandHandler(
            _files.Object,
            UserContextFactory.Create(userId),
            new RequestContext { DeviceName = deviceName },
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty(),
            NullLogger<GetUploadUrlCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_PersistsDraftFileWithUploader()
    {
        UploadFileEntity? captured = null;
        _files.Setup(s => s.AddToStorage(It.IsAny<UploadFileEntity>()))
            .Callback<UploadFileEntity>(f =>
            {
                f.Id = Guid.NewGuid();
                captured = f;
            })
            .ReturnsAsync((UploadFileEntity f) => f);

        var sut = new GetUploadUrlCommandHandler(
            _files.Object,
            UserContextFactory.Create(42),
            new RequestContext { DeviceName = "Pixel" },
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty(),
            NullLogger<GetUploadUrlCommandHandler>.Instance);

        await sut.Handle(new GetUploadUrlCommand { Type = UploadFileType.CloudFile }, default);

        captured.Should().NotBeNull();
        captured!.Type.Should().Be(UploadFileType.CloudFile);
        captured.Uploaders.Should().Contain(42);
        captured.UploadDeviceName.Should().Be("Pixel");
    }

    [Fact]
    public async Task Handle_ReturnsUploadUrlWithFileId()
    {
        var response = await CreateSut().Handle(new GetUploadUrlCommand { Type = UploadFileType.CloudFile }, default);

        response.FileId.Should().NotBeNullOrWhiteSpace();
        response.Url.Should().Contain("/upload/").And.Contain(response.FileId);
    }

    [Fact]
    public async Task Handle_UsesExternalEndpointWhenConfigured()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.AddToStorage(It.IsAny<UploadFileEntity>()))
            .ReturnsAsync((UploadFileEntity f) =>
            {
                f.Id = fileId;
                return f;
            });

        var sut = new GetUploadUrlCommandHandler(
            _files.Object,
            UserContextFactory.Create(42),
            new RequestContext { DeviceName = "Pixel" },
            new RunSettings(),
            TestConfiguration.With(("ExternalEndpoint:Host", "https://barkcloud.io")),
            NullLogger<GetUploadUrlCommandHandler>.Instance);

        var response = await sut.Handle(new GetUploadUrlCommand { Type = UploadFileType.CloudFile }, default);

        response.Url.Should().StartWith("https://barkcloud.io/web/upload/");
    }
}
