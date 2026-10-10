using BarkCloud.Drive.Engine.Tests.Fakes;

using DokanNet;
using Grpc.Core;

using Xunit;

using FileAccess = DokanNet.FileAccess;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class BarkCloudFileSystemTests
{
    [Fact]
    public void CreateFile_returns_network_error_when_path_listing_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        cloud.Invoker.FailNext(FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "Сервер недоступен")));

        var status = fileSystem.CreateFile("\\missing.txt", FileAccess.WriteData, FileShare.Read,
            FileMode.Create, FileOptions.None, FileAttributes.Normal, new FakeDokanFileInfo());

        ((long)status).Should().Be(0xC00000C4L);
        cloud.Invoker.Calls.Should().ContainSingle(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed);
        cloud.Invoker.Calls.Should().NotContain(call =>
            call.MethodFullName == FakeCloudMethods.GetUploadUrl
            || call.MethodFullName == FakeCloudMethods.AttachFile);
        Directory.GetFiles(temp.Path("write")).Should().BeEmpty();
    }

    [Fact]
    public void DeleteDirectory_returns_network_error_when_listing_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("directory");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        gateway.ListDirectory("");
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        cloud.Invoker.FailNext(FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "Сервер недоступен")));

        var status = fileSystem.DeleteDirectory("\\directory", new FakeDokanFileInfo());

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        cloud.Invoker.Calls.Should().ContainSingle(call =>
            call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed
            && ((BarkCloud.Proto.Files.ListDirectoryRequest)call.Request).DirectoryId == directoryId);
    }

    [Fact]
    public void MoveFile_returns_file_exists_for_directory_name_conflict()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "source.txt", [1]);
        cloud.AddFile("", "target.txt", [2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        var status = fileSystem.MoveFile("\\source.txt", "\\target.txt", replace: false,
            new FakeDokanFileInfo());

        status.Should().Be(DokanResult.FileExists);
    }

    [Fact]
    public void ToStatus_maps_server_errors_and_disk_full_hresult()
    {
        BarkCloudFileSystem.ToStatus(FakeCallInvoker.BusinessError(ServerErrors.FileEntryNotFound))
            .Should().Be(DokanResult.FileNotFound);
        BarkCloudFileSystem.ToStatus(FakeCallInvoker.BusinessError(ServerErrors.DirectoryNotFound))
            .Should().Be(DokanResult.PathNotFound);
        BarkCloudFileSystem.ToStatus(FakeCallInvoker.BusinessError(ServerErrors.DirectoryNameConflict.ToLowerInvariant()))
            .Should().Be(DokanResult.FileExists);
        BarkCloudFileSystem.ToStatus(FakeCallInvoker.BusinessError(ServerErrors.CloudAccessDenied))
            .Should().Be(DokanResult.AccessDenied);
        BarkCloudFileSystem.ToStatus(new RpcException(new Status(StatusCode.PermissionDenied, "Доступ запрещён")))
            .Should().Be(DokanResult.AccessDenied);
        BarkCloudFileSystem.ToStatus(new IOException("Диск заполнен", unchecked((int)0x80070070)))
            .Should().Be(DokanResult.DiskFull);
        BarkCloudFileSystem.ToStatus(new IOException("Диск заполнен", unchecked((int)0x80070027)))
            .Should().Be(DokanResult.DiskFull);
        BarkCloudFileSystem.ToStatus(new OperationCanceledException())
            .Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        BarkCloudFileSystem.ToStatus(new RpcException(new Status(StatusCode.DeadlineExceeded, "Истёк срок вызова")))
            .Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
    }

    [Fact]
    public void ToStatus_maps_circular_move_to_access_denied()
    {
        BarkCloudFileSystem.ToStatus(FakeCallInvoker.BusinessError(ServerErrors.CircularMove))
            .Should().Be(DokanResult.AccessDenied);
    }

    [Fact]
    public void WriteFile_returns_status_instead_of_throwing_when_stream_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var tempPath = temp.Path("closed.bin");
        var stream = new FileStream(tempPath, FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.Read);
        stream.Dispose();
        var info = new FakeDokanFileInfo
        {
            Context = new WriteSession
            {
                Name = "closed.bin",
                DirectoryId = "",
                TempPath = tempPath,
                Stream = stream,
            },
        };

        var status = fileSystem.WriteFile("\\closed.bin", [1], out var bytesWritten, 0, info);

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        bytesWritten.Should().Be(0);
    }
}
