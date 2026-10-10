using System.Text.Json;

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
    public void Two_writers_share_a_local_file_and_persist_once_after_both_cleanups()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        var first = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\shared.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, first).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\shared.bin", [1, 2], out var firstBytes, 0, first).Should().Be(DokanResult.Success);
        firstBytes.Should().Be(2);

        var second = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\shared.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, second).Should().Be(DokanResult.Success);
        ((HandleContext)first.Context!).File.Should().BeSameAs(((HandleContext)second.Context!).File);
        fileSystem.WriteFile("\\shared.bin", [3], out _, 2, second).Should().Be(DokanResult.Success);

        fileSystem.Cleanup("\\shared.bin", first);
        var shared = ((HandleContext)first.Context!).File!;
        fileSystem.Cleanup("\\shared.bin", first);
        shared.ActiveCount.Should().Be(1);
        fileSystem.Cleanup("\\shared.bin", second);
        cloud.HttpHandler.Calls.Count(call => call.Method == "POST").Should().Be(1);

        fileSystem.CloseFile("\\shared.bin", first);
        fileSystem.CloseFile("\\shared.bin", second);
    }

    [Fact]
    public void Reopen_between_cleanup_and_close_uses_the_same_local_file()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var first = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\reopen.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, first).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\reopen.bin", [7], out _, 0, first).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\reopen.bin", first);

        var second = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\reopen.bin", FileAccess.ReadData | FileAccess.GenericRead, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, second).Should().Be(DokanResult.Success);
        ((HandleContext)first.Context!).File.Should().BeSameAs(((HandleContext)second.Context!).File);
        fileSystem.CloseFile("\\reopen.bin", first);

        var buffer = new byte[1];
        fileSystem.ReadFile("\\reopen.bin", buffer, out var bytesRead, 0, second).Should().Be(DokanResult.Success);
        bytesRead.Should().Be(1);
        buffer.Should().Equal([7]);
        fileSystem.Cleanup("\\reopen.bin", second);
        fileSystem.CloseFile("\\reopen.bin", second);
        cloud.HttpHandler.Calls.Count(call => call.Method == "POST").Should().Be(1);
    }

    [Fact]
    public void Reader_opened_before_writer_reads_the_local_copy()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "shared.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\shared.bin", FileAccess.ReadData | FileAccess.GenericRead, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\shared.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\shared.bin", [9], out _, 1, writer).Should().Be(DokanResult.Success);

        var buffer = new byte[3];
        fileSystem.ReadFile("\\shared.bin", buffer, out var bytesRead, 0, reader).Should().Be(DokanResult.Success);
        bytesRead.Should().Be(3);
        buffer.Should().Equal([1, 9, 3]);

        fileSystem.Cleanup("\\shared.bin", writer);
        fileSystem.CloseFile("\\shared.bin", writer);
        fileSystem.CloseFile("\\shared.bin", reader);
    }

    [Fact]
    public void Local_CreateNew_fails_and_Create_truncates_the_shared_copy()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var first = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\local.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, first).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\local.bin", [1, 2, 3], out _, 0, first).Should().Be(DokanResult.Success);

        fileSystem.CreateFile("\\local.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, new FakeDokanFileInfo()).Should().Be(DokanResult.FileExists);

        var second = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\local.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Create,
            FileOptions.None, FileAttributes.Normal, second).Should().Be(DokanResult.AlreadyExists);
        ((HandleContext)first.Context!).File.Should().BeSameAs(((HandleContext)second.Context!).File);
        fileSystem.GetFileInformation("\\local.bin", out var info, second).Should().Be(DokanResult.Success);
        info.Length.Should().Be(0);

        var third = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\local.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.OpenOrCreate,
            FileOptions.None, FileAttributes.Normal, third).Should().Be(DokanResult.AlreadyExists);
        fileSystem.Cleanup("\\local.bin", first);
        fileSystem.Cleanup("\\local.bin", second);
        fileSystem.Cleanup("\\local.bin", third);
        fileSystem.CloseFile("\\local.bin", first);
        fileSystem.CloseFile("\\local.bin", second);
        fileSystem.CloseFile("\\local.bin", third);
        cloud.HttpHandler.Calls.Should().BeEmpty();
    }

    [Fact]
    public void DeletePending_from_one_handle_is_kept_until_all_handles_cleanup()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var first = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\delete.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, first).Should().Be(DokanResult.Success);
        var second = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\delete.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, second).Should().Be(DokanResult.Success);
        var local = ((HandleContext)first.Context!).File!;

        first.DeletePending = true;
        fileSystem.Cleanup("\\delete.bin", first);
        local.DeleteOnClose.Should().BeTrue();
        fileSystem.Cleanup("\\delete.bin", second);
        local.DeleteOnClose.Should().BeTrue();

        var recreated = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\delete.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, recreated).Should().Be(DokanResult.Success);
        fileSystem.CloseFile("\\delete.bin", first);
        fileSystem.CloseFile("\\delete.bin", second);
        fileSystem.Cleanup("\\delete.bin", recreated);
        fileSystem.CloseFile("\\delete.bin", recreated);
    }

    [Fact]
    public void Paging_write_after_cleanup_is_persisted_from_close_file()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\paging.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\paging.bin", [1], out _, 0, info).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\paging.bin", info);
        info.PagingIo = true;
        fileSystem.WriteFile("\\paging.bin", [2], out _, 1, info).Should().Be(DokanResult.Success);

        fileSystem.CloseFile("\\paging.bin", info);

        cloud.HttpHandler.Calls.Count(call => call.Method == "POST").Should().Be(2);
    }

    [Fact]
    public void Empty_new_file_stays_as_a_placeholder_until_written()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\empty.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var local = ((HandleContext)info.Context!).File!;
        fileSystem.Cleanup("\\empty.bin", info);
        fileSystem.CloseFile("\\empty.bin", info);
        local.Kind.Should().Be(LocalFileKind.Placeholder);
        File.Exists(local.TempPath).Should().BeTrue();
        cloud.HttpHandler.Calls.Should().BeEmpty();

        fileSystem.FindFiles("\\", out var files, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        files.Should().Contain(file => file.FileName == "empty.bin");
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\empty.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\empty.bin", [5], out _, 0, writer).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\empty.bin", writer);
        fileSystem.CloseFile("\\empty.bin", writer);
        cloud.HttpHandler.Calls.Count(call => call.Method == "POST").Should().Be(1);
    }

    [Fact]
    public void Pending_local_file_denies_writes_and_allows_reads()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var path = temp.Path("pending.bin");
        File.WriteAllBytes(path, [4, 5]);
        var local = new LocalFile
        {
            Path = "\\pending.bin",
            Name = "pending.bin",
            DirectoryId = "",
            TempPath = path,
            Stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.Read),
            Kind = LocalFileKind.Pending,
            Hydrated = true,
        };
        fileSystem.RegisterLocalFile(local).Should().BeTrue();

        foreach (var mode in new[] { FileMode.Open, FileMode.Create, FileMode.OpenOrCreate, FileMode.Truncate, FileMode.Append })
            fileSystem.CreateFile("\\pending.bin", FileAccess.WriteData, FileShare.ReadWrite, mode,
                FileOptions.None, FileAttributes.Normal, new FakeDokanFileInfo()).Should().Be(DokanResult.AccessDenied);
        fileSystem.CreateFile("\\pending.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, new FakeDokanFileInfo()).Should().Be(DokanResult.FileExists);
        fileSystem.DeleteFile("\\pending.bin", new FakeDokanFileInfo()).Should().Be(DokanResult.AccessDenied);
        fileSystem.MoveFile("\\pending.bin", "\\renamed.bin", replace: false, new FakeDokanFileInfo())
            .Should().Be(DokanResult.AccessDenied);
        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\pending.bin", FileAccess.ReadData | FileAccess.GenericRead, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        var buffer = new byte[2];
        fileSystem.ReadFile("\\pending.bin", buffer, out var bytesRead, 0, reader).Should().Be(DokanResult.Success);
        bytesRead.Should().Be(2);
        buffer.Should().Equal([4, 5]);
        fileSystem.WriteFile("\\pending.bin", [6], out _, 0, reader).Should().Be(DokanResult.AccessDenied);
        fileSystem.Cleanup("\\pending.bin", reader);
        fileSystem.CloseFile("\\pending.bin", reader);
        var appendReader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\pending.bin", FileAccess.ReadData | FileAccess.GenericRead, FileShare.ReadWrite,
            FileMode.Append, FileOptions.None, FileAttributes.Normal, appendReader).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\pending.bin", appendReader);
        fileSystem.CloseFile("\\pending.bin", appendReader);
        fileSystem.FindFiles("\\", out var files, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        files.Should().Contain(file => file.FileName == "pending.bin");
    }

    [Fact]
    public void Persist_uploads_then_deletes_old_entry_then_attaches_with_original_name()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "replace.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\replace.bin", [4, 5, 6]);

        fileSystem.Cleanup("\\replace.bin", info);
        fileSystem.CloseFile("\\replace.bin", info);

        cloud.Operations.Where(operation => operation == "HTTP:POST"
                || operation == $"RPC:{FakeCloudMethods.DeleteFileEntry}"
                || operation == $"RPC:{FakeCloudMethods.AttachFile}")
            .Should().Equal("HTTP:POST", $"RPC:{FakeCloudMethods.DeleteFileEntry}", $"RPC:{FakeCloudMethods.AttachFile}");
        var saved = gateway.ListDirectory("").Files.Should().ContainSingle().Subject;
        saved.Entry.Name.Should().Be("replace.bin");
        local.ExistingEntryId.Should().Be(saved.Entry.Id);
        local.Op!.Stage.Should().Be(PersistStage.Done);
    }

    [Fact]
    public void Persist_recovers_a_lost_attach_response_without_uploading_or_attaching_twice()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "retry-attach.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (first, local) = OpenWritableAndWrite(fileSystem, "\\retry-attach.bin", [4, 5, 6]);
        cloud.Invoker.LoseNextResponseAfterEffect(FakeCloudMethods.AttachFile);
        cloud.Invoker.FailNext(FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "Листинг недоступен")));

        fileSystem.Cleanup("\\retry-attach.bin", first);

        local.Op!.Stage.Should().Be(PersistStage.Attach);
        local.Op.FileId.Should().NotBeNullOrEmpty();
        local.Op.OldEntryDeleted.Should().BeTrue();
        local.Op.AttachAttempted.Should().BeTrue();
        local.Op.AttachCompleted.Should().BeFalse();
        fileSystem.LastSyncError.Should().Contain("retry-attach.bin");

        var retry = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\retry-attach.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, retry).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\retry-attach.bin", retry);
        fileSystem.CloseFile("\\retry-attach.bin", retry);
        fileSystem.CloseFile("\\retry-attach.bin", first);

        cloud.HttpHandler.Calls.Count(call => call.Method == "POST").Should().Be(1);
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.AttachFile).Should().Be(1);
        var saved = gateway.ListDirectory("").Files.Should().ContainSingle().Subject;
        saved.Entry.Name.Should().Be("retry-attach.bin");
        saved.File.Id.Should().Be(local.Op!.FileId);
        local.ExistingEntryId.Should().Be(saved.Entry.Id);
        fileSystem.LastSyncError.Should().Contain("retry-attach.bin");
    }

    [Fact]
    public void Persist_retries_a_delete_whose_response_was_lost()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "retry-delete.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (first, local) = OpenWritableAndWrite(fileSystem, "\\retry-delete.bin", [4, 5, 6]);
        cloud.Invoker.LoseNextResponseAfterEffect(FakeCloudMethods.DeleteFileEntry);

        fileSystem.Cleanup("\\retry-delete.bin", first);

        local.Op!.Stage.Should().Be(PersistStage.DeleteOld);
        local.Op.FileId.Should().NotBeNullOrEmpty();
        var retry = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\retry-delete.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, retry).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\retry-delete.bin", retry);
        fileSystem.CloseFile("\\retry-delete.bin", retry);
        fileSystem.CloseFile("\\retry-delete.bin", first);

        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntry).Should().Be(2);
        cloud.HttpHandler.Calls.Count(call => call.Method == "POST").Should().Be(1);
        var saved = gateway.ListDirectory("").Files.Should().ContainSingle().Subject;
        saved.Entry.Name.Should().Be("retry-delete.bin");
        local.ExistingEntryId.Should().Be(saved.Entry.Id);
    }

    [Fact]
    public void Attach_business_rejection_restores_the_old_entry()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var original = cloud.AddFile("", "restore.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\restore.bin", [4, 5, 6]);
        cloud.Invoker.FailNext(FakeCloudMethods.AttachFile,
            FakeCallInvoker.BusinessError(ServerErrors.DirectoryNotFound));

        fileSystem.Cleanup("\\restore.bin", info);

        var restored = gateway.ListDirectory("").Files.Should().ContainSingle().Subject;
        restored.Entry.Id.Should().Be(original.EntryId);
        restored.Entry.Name.Should().Be("restore.bin");
        local.ExistingEntryId.Should().Be(original.EntryId);
        local.Op!.Stage.Should().Be(PersistStage.DeleteOld);
        local.Op.OldEntryDeleted.Should().BeFalse();
        fileSystem.CloseFile("\\restore.bin", info);
        fileSystem.LastSyncError.Should().Contain("restore.bin");
    }

    [Fact]
    public void Persist_reports_attach_and_restore_errors_together()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "restore-failed.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\restore-failed.bin", [4, 5, 6]);
        cloud.Invoker.FailNext(FakeCloudMethods.AttachFile,
            FakeCallInvoker.BusinessError(ServerErrors.DirectoryNotFound));
        cloud.Invoker.FailNext(FakeCloudMethods.RestoreFromTrash,
            new RpcException(new Status(StatusCode.Unavailable, "Restore failed")));

        fileSystem.Cleanup("\\restore-failed.bin", info);

        fileSystem.LastSyncError.Should().Contain("restore-failed.bin")
            .And.Contain("Операция отклонена сервером")
            .And.Contain("Restore failed");
        local.Op!.Stage.Should().Be(PersistStage.Attach);
        local.Op.OldEntryDeleted.Should().BeTrue();
        File.Exists(local.TempPath).Should().BeTrue();
        fileSystem.CloseFile("\\restore-failed.bin", info);
    }

    [Fact]
    public async Task Repeated_persist_replaces_the_latest_entry_and_preserves_content()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "second-save.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (first, local) = OpenWritableAndWrite(fileSystem, "\\second-save.bin", [4, 5, 6]);
        fileSystem.Cleanup("\\second-save.bin", first);
        var firstSave = gateway.ListDirectory("").Files.Should().ContainSingle().Subject;

        var second = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\second-save.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, second).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\second-save.bin", [7, 8, 9], out _, 0, second).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\second-save.bin", second);
        fileSystem.CloseFile("\\second-save.bin", first);
        fileSystem.CloseFile("\\second-save.bin", second);

        var saved = gateway.ListDirectory("").Files.Should().ContainSingle().Subject;
        saved.Entry.Name.Should().Be("second-save.bin");
        saved.Entry.Id.Should().NotBe(firstSave.Entry.Id);
        local.ExistingEntryId.Should().Be(saved.Entry.Id);
        var downloaded = temp.Path("saved.bin");
        await gateway.DownloadToAsync(saved.File.Id, downloaded);
        File.ReadAllBytes(downloaded).Should().Equal([7, 8, 9]);
    }

    [Fact]
    public void Persist_operation_state_round_trips_through_json()
    {
        var operation = new PersistOperation
        {
            Stage = PersistStage.Attach,
            FileId = "uploaded-file-id",
            OldEntryDeleted = true,
            AttachAttempted = true,
            SupersededFileId = "previously-uploaded-file-id",
        };

        var restored = JsonSerializer.Deserialize<PersistOperation>(JsonSerializer.Serialize(operation));

        restored.Should().BeEquivalentTo(operation);
    }

    [Fact]
    public void Failed_persist_keeps_the_local_copy_for_the_outbox_step()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\retry.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var local = ((HandleContext)info.Context!).File!;
        fileSystem.WriteFile("\\retry.bin", [8], out _, 0, info).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\retry.bin", info);
        fileSystem.CloseFile("\\retry.bin", info);

        local.KeepData.Should().BeTrue();
        File.Exists(local.TempPath).Should().BeTrue();
        fileSystem.LastSyncError.Should().Contain("retry.bin");
    }

    [Fact]
    public void Truncating_existing_file_to_empty_keeps_cloud_version_and_reports_error()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var original = cloud.AddFile("", "existing.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\existing.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Truncate,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\existing.bin", info);
        fileSystem.CloseFile("\\existing.bin", info);

        fileSystem.LastSyncError.Should().Contain("пустые файлы");
        cloud.HttpHandler.Calls.Should().BeEmpty();
        gateway.ListDirectory("").Files.Should().ContainSingle(file => file.File.Id == original.FileId);
    }

    [Fact]
    public void WriteFile_returns_status_instead_of_throwing_when_temp_file_is_missing()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var tempPath = temp.Path("missing.bin");
        var stream = new FileStream(temp.Path("closed.bin"), FileMode.Create,
            System.IO.FileAccess.ReadWrite, FileShare.Read);
        stream.Dispose();
        var info = new FakeDokanFileInfo
        {
            Context = new HandleContext
            {
                File = new LocalFile
                {
                    Path = "\\closed.bin",
                    Name = "closed.bin",
                    DirectoryId = "",
                    TempPath = tempPath,
                    Stream = stream,
                    Kind = LocalFileKind.Writable,
                },
            },
        };

        var status = fileSystem.WriteFile("\\closed.bin", [1], out var bytesWritten, 0, info);

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        bytesWritten.Should().Be(0);
    }

    [Fact]
    public async Task New_write_after_failed_attach_uploads_new_bytes_and_keeps_deleted_old_entry()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "edited.bin", [1, 1, 1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (first, local) = OpenWritableAndWrite(fs, "\\edited.bin", [4, 4, 4]);
        for (var i = 0; i < 2; i++)
            cloud.Invoker.FailNext(FakeCloudMethods.AttachFile,
                new RpcException(new Status(StatusCode.Unavailable, "offline")));
        fs.Cleanup("\\edited.bin", first);
        local.Op!.OldEntryDeleted.Should().BeTrue();
        var (second, _) = OpenWritableAndWrite(fs, "\\edited.bin", [7, 7, 7]);
        fs.Cleanup("\\edited.bin", second);
        fs.CloseFile("\\edited.bin", first);
        fs.CloseFile("\\edited.bin", second);
        var saved = gateway.ListDirectory("").Files.Should().ContainSingle().Subject;
        await gateway.DownloadToAsync(saved.File.Id, temp.Path("saved.bin"));
        File.ReadAllBytes(temp.Path("saved.bin")).Should().Equal([7, 7, 7]);
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntry).Should().Be(1);
    }

    [Fact]
    public async Task First_paging_write_after_cleanup_uploads_from_close()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "mapped.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = new FakeDokanFileInfo();
        fs.CreateFile("\\mapped.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        fs.Cleanup("\\mapped.bin", info);
        info.PagingIo = true;
        fs.WriteFile("\\mapped.bin", [7, 8, 9], out _, 0, info).Should().Be(DokanResult.Success);
        fs.CloseFile("\\mapped.bin", info);
        cloud.HttpHandler.Calls.Count(call => call.Method == "POST").Should().Be(1);
        var saved = gateway.ListDirectory("").Files.Should().ContainSingle().Subject;
        await gateway.DownloadToAsync(saved.File.Id, temp.Path("saved.bin"));
        File.ReadAllBytes(temp.Path("saved.bin")).Should().Equal([7, 8, 9]);
    }

    [Fact]
    public async Task New_write_after_uncertain_successful_attach_replaces_that_entry_without_duplicate()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "uncertain.bin", [1, 1, 1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (first, _) = OpenWritableAndWrite(fs, "\\uncertain.bin", [4, 4, 4]);
        cloud.Invoker.LoseNextResponseAfterEffect(FakeCloudMethods.AttachFile);
        cloud.Invoker.FailNext(FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));
        fs.Cleanup("\\uncertain.bin", first);
        var (second, _) = OpenWritableAndWrite(fs, "\\uncertain.bin", [7, 7, 7]);
        fs.Cleanup("\\uncertain.bin", second);
        fs.CloseFile("\\uncertain.bin", first);
        fs.CloseFile("\\uncertain.bin", second);
        var saved = gateway.ListDirectory("").Files.Should().ContainSingle().Subject;
        saved.Entry.Name.Should().Be("uncertain.bin");
        await gateway.DownloadToAsync(saved.File.Id, temp.Path("saved.bin"));
        File.ReadAllBytes(temp.Path("saved.bin")).Should().Equal([7, 7, 7]);
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("EndOfFile")]
    [InlineData("Allocation")]
    [InlineData("Create")]
    [InlineData("Truncate")]
    public void Each_data_mutation_invalidates_the_uploaded_blob_without_forgetting_deletion(string mutation)
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "mutation.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (info, local) = OpenWritableAndWrite(fs, "\\mutation.bin", [4, 5, 6]);
        for (var i = 0; i < 2; i++)
            cloud.Invoker.FailNext(FakeCloudMethods.AttachFile,
                new RpcException(new Status(StatusCode.Unavailable, "offline")));
        fs.Cleanup("\\mutation.bin", info);
        switch (mutation)
        {
            case "Write": fs.WriteFile("\\mutation.bin", [7], out _, 0, info).Should().Be(DokanResult.Success); break;
            case "EndOfFile": fs.SetEndOfFile("\\mutation.bin", 2, info).Should().Be(DokanResult.Success); break;
            case "Allocation": fs.SetAllocationSize("\\mutation.bin", 2, info).Should().Be(DokanResult.Success); break;
            default:
                var reopened = new FakeDokanFileInfo();
                fs.CreateFile("\\mutation.bin", FileAccess.WriteData, FileShare.ReadWrite,
                    mutation == "Create" ? FileMode.Create : FileMode.Truncate,
                    FileOptions.None, FileAttributes.Normal, reopened)
                    .Should().Be(mutation == "Create" ? DokanResult.AlreadyExists : DokanResult.Success);
                fs.Cleanup("\\mutation.bin", reopened);
                fs.CloseFile("\\mutation.bin", reopened);
                break;
        }
        local.Op!.Stage.Should().Be(PersistStage.Upload);
        local.Op.FileId.Should().BeNull();
        local.Op.OldEntryDeleted.Should().BeTrue();
        info.DeletePending = true;
        fs.Cleanup("\\mutation.bin", info);
        fs.CloseFile("\\mutation.bin", info);
    }

    [Fact]
    public void Written_placeholder_is_removed_after_save_and_cloud_deletion()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = new FakeDokanFileInfo();
        fs.CreateFile("\\placeholder.txt", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var local = ((HandleContext)info.Context!).File!;
        fs.Cleanup("\\placeholder.txt", info);
        fs.CloseFile("\\placeholder.txt", info);
        var (writer, _) = OpenWritableAndWrite(fs, "\\placeholder.txt", [5]);
        fs.Cleanup("\\placeholder.txt", writer);
        fs.CloseFile("\\placeholder.txt", writer);
        local.Kind.Should().Be(LocalFileKind.Writable);
        File.Exists(local.TempPath).Should().BeFalse();
        gateway.DeleteFileEntry(local.ExistingEntryId!);
        fs.FindFiles("\\", out var files, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        files.Should().BeEmpty();
    }

    [Fact]
    public async Task Reads_and_listing_finish_while_upload_is_blocked()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "busy.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (writer, _) = OpenWritableAndWrite(fs, "\\busy.bin", [7, 8, 9]);
        var reader = new FakeDokanFileInfo();
        fs.CreateFile("\\busy.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        fs.Cleanup("\\busy.bin", reader);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = cloud.HttpHandler.Responder;
        cloud.HttpHandler.Responder = async (request, cancellation) =>
        {
            if (request.Method == System.Net.Http.HttpMethod.Post)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return await responder(request, cancellation);
        };
        var saving = Task.Run(() => fs.Cleanup("\\busy.bin", writer));
        var lateReader = new FakeDokanFileInfo();
        Task? reading = null;
        Task? listing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reading = Task.Run(() =>
            {
                fs.CreateFile("\\busy.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
                    FileOptions.None, FileAttributes.Normal, lateReader).Should().Be(DokanResult.Success);
                var buffer = new byte[3];
                fs.ReadFile("\\busy.bin", buffer, out var count, 0, lateReader).Should().Be(DokanResult.Success);
                count.Should().Be(3);
                buffer.Should().Equal([7, 8, 9]);
                fs.GetFileInformation("\\busy.bin", out var metadata, lateReader).Should().Be(DokanResult.Success);
                metadata.Length.Should().Be(3);
            });
            listing = Task.Run(() =>
            {
                fs.FindFiles("\\", out var files, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
                files.Should().ContainSingle(file => file.FileName == "busy.bin" && file.Length == 3);
            });
            await Task.WhenAll(reading, listing).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
            await saving;
            if (reading is not null && listing is not null)
                await Task.WhenAll(reading, listing);
            fs.Cleanup("\\busy.bin", lateReader);
            fs.CloseFile("\\busy.bin", lateReader);
            fs.CloseFile("\\busy.bin", writer);
            fs.CloseFile("\\busy.bin", reader);
        }
    }

    [Theory]
    [InlineData(FileMode.Open)]
    [InlineData(FileMode.Append)]
    public async Task Racing_writer_registration_returns_success_for_open_and_append(FileMode mode)
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "race.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = cloud.HttpHandler.Responder;
        var downloads = 0;
        cloud.HttpHandler.Responder = async (request, cancellation) =>
        {
            if (request.Method == System.Net.Http.HttpMethod.Get && Interlocked.Increment(ref downloads) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return await responder(request, cancellation);
        };
        var first = new FakeDokanFileInfo();
        var second = new FakeDokanFileInfo();
        var opening = Task.Run(() => fs.CreateFile("\\race.bin", FileAccess.WriteData, FileShare.ReadWrite,
            mode, FileOptions.None, FileAttributes.Normal, first));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fs.CreateFile("\\race.bin", FileAccess.WriteData, FileShare.ReadWrite,
                mode, FileOptions.None, FileAttributes.Normal, second).Should().Be(DokanResult.Success);
        }
        finally
        {
            release.TrySetResult();
            (await opening).Should().Be(DokanResult.Success);
            fs.Cleanup("\\race.bin", first);
            fs.Cleanup("\\race.bin", second);
            fs.CloseFile("\\race.bin", first);
            fs.CloseFile("\\race.bin", second);
        }
    }

    [Fact]
    public void Delete_after_uncertain_attach_queues_the_server_entry_for_deletion()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var attached = cloud.AddFile("", "server-entry.bin", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = new FakeDokanFileInfo();
        fs.CreateFile("\\ghost.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var local = ((HandleContext)info.Context!).File!;
        local.Op = new PersistOperation
        {
            Stage = PersistStage.Attach,
            FileId = attached.FileId,
            AttachAttempted = true,
        };
        info.DeletePending = true;

        fs.Cleanup("\\ghost.bin", info);
        fs.CloseFile("\\ghost.bin", info);
        gateway.FlushPending();

        cloud.Invoker.Calls.Should().Contain(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries);
        gateway.ListDirectory("").Files.Should().NotContain(file => file.File.Id == attached.FileId);
        fs.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void Delete_after_uncertain_attach_records_lookup_failure_without_throwing_from_cleanup()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var attached = cloud.AddFile("", "server-entry.bin", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = new FakeDokanFileInfo();
        fs.CreateFile("\\ghost.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var local = ((HandleContext)info.Context!).File!;
        local.Op = new PersistOperation
        {
            Stage = PersistStage.Attach,
            FileId = attached.FileId,
            AttachAttempted = true,
        };
        cloud.Invoker.FailNext(FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "листинг недоступен")));
        info.DeletePending = true;

        var cleanup = () => fs.Cleanup("\\ghost.bin", info);
        cleanup.Should().NotThrow();

        fs.LastSyncError.Should().Contain("листинг недоступен");
        fs.CloseFile("\\ghost.bin", info);
    }

    [Fact]
    public async Task Truncate_rechecks_pending_state_after_waiting_for_local_sync()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [1, 2]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        var local = new LocalFile
        {
            Path = "\\race.bin",
            Name = "race.bin",
            DirectoryId = "",
            TempPath = dataPath,
            Kind = LocalFileKind.Writable,
            Hydrated = true,
        };
        fs.RegisterLocalFile(local).Should().BeTrue();

        Monitor.Enter(local.Sync);
        Task<NtStatus> opening;
        try
        {
            opening = Task.Run(() => fs.CreateFile("\\race.bin", FileAccess.WriteData, FileShare.ReadWrite,
                FileMode.Truncate, FileOptions.None, FileAttributes.Normal, new FakeDokanFileInfo()));
            SpinWait.SpinUntil(() => local.OpenCount == 1, TimeSpan.FromSeconds(5)).Should().BeTrue();
            var entry = outbox.Add(local, "server", "42");
            fs.RegisterOutboxPending(entry, local);
        }
        finally
        {
            Monitor.Exit(local.Sync);
        }

        (await opening.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(DokanResult.AccessDenied);
        File.ReadAllBytes(local.TempPath).Should().Equal([1, 2]);
        local.OpenCount.Should().Be(0);
    }

    private static (FakeDokanFileInfo Info, LocalFile File) OpenWritableAndWrite(
        BarkCloudFileSystem fileSystem, string path, byte[] content)
    {
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile(path, FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var file = ((HandleContext)info.Context!).File!;
        fileSystem.WriteFile(path, content, out var bytesWritten, 0, info).Should().Be(DokanResult.Success);
        bytesWritten.Should().Be(content.Length);
        return (info, file);
    }
}
