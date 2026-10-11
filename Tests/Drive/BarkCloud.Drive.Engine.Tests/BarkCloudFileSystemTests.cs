using System.Text.Json;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Drive.Engine.Tests.Fakes;

using DokanNet;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

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
    public void DeleteDirectory_uses_fresh_listing_instead_of_cached_empty_listing()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("directory");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        gateway.ListDirectory(directoryId).Files.Should().BeEmpty();
        cloud.AddFile(directoryId, "new.txt");
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        var status = fileSystem.DeleteDirectory("\\directory", new FakeDokanFileInfo());

        status.Should().Be(DokanResult.DirectoryNotEmpty);
        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.DeleteDirectory);
    }

    [Fact]
    public void DeleteDirectory_returns_directory_not_empty_for_subdirectory()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("directory");
        cloud.AddDirectory("nested", directoryId);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        fileSystem.DeleteDirectory("\\directory", new FakeDokanFileInfo()).Should()
            .Be(DokanResult.DirectoryNotEmpty);
    }

    [Fact]
    public void DeleteDirectory_returns_directory_not_empty_for_local_file_under_path()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddDirectory("directory");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        fileSystem.RegisterLocalFile(new LocalFile
        {
            Path = "\\directory\\nested\\draft.txt",
            Name = "draft.txt",
            DirectoryId = "removed-nested-directory",
            TempPath = temp.Path("write", "draft.txt"),
        }).Should().BeTrue();

        fileSystem.DeleteDirectory("\\directory", new FakeDokanFileInfo()).Should()
            .Be(DokanResult.DirectoryNotEmpty);
    }

    [Fact]
    public void DeleteDirectory_returns_directory_not_empty_for_pending_file()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("directory");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        fileSystem.RegisterOutboxPending(new UploadOutboxEntry
        {
            Id = "pending-id",
            Name = "pending.txt",
            DirectoryId = directoryId,
            DataPath = temp.Path("pending.bin"),
        });

        fileSystem.DeleteDirectory("\\directory", new FakeDokanFileInfo()).Should()
            .Be(DokanResult.DirectoryNotEmpty);
    }

    [Fact]
    public void DeleteDirectory_returns_directory_not_empty_for_unowned_outbox_file()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("directory");
        var dataPath = temp.Path("draft.bin");
        File.WriteAllBytes(dataPath, [1, 2, 3]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        outbox.Add(new LocalFile
        {
            Path = "\\directory\\draft.txt",
            Name = "draft.txt",
            DirectoryId = directoryId,
            TempPath = dataPath,
        }, "server-key", ownerUserId: null);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox,
            "server-key", ownerUserId: () => null);

        fileSystem.DeleteDirectory("\\directory", new FakeDokanFileInfo()).Should()
            .Be(DokanResult.DirectoryNotEmpty);
        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.DeleteDirectory);
    }

    [Fact]
    public void DeleteDirectory_cleanup_flushes_file_deletions_before_deleting_empty_directory()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("directory");
        var file = cloud.AddFile(directoryId, "file.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var fileInfo = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\directory\\file.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, fileInfo).Should().Be(DokanResult.Success);
        fileSystem.DeleteFile("\\directory\\file.txt", fileInfo).Should().Be(DokanResult.Success);
        fileInfo.DeletePending = true;
        fileSystem.Cleanup("\\directory\\file.txt", fileInfo);
        fileSystem.CloseFile("\\directory\\file.txt", fileInfo);
        var directoryInfo = new FakeDokanFileInfo { IsDirectory = true };

        fileSystem.DeleteDirectory("\\directory", directoryInfo).Should().Be(DokanResult.Success);
        directoryInfo.DeletePending = true;
        fileSystem.Cleanup("\\directory", directoryInfo);

        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.DeleteDirectory).Should().Be(1);
        cloud.Invoker.Calls
            .Where(call => call.MethodFullName is FakeCloudMethods.DeleteFileEntries or FakeCloudMethods.DeleteDirectory)
            .Select(call => call.MethodFullName)
            .Should().Equal(FakeCloudMethods.DeleteFileEntries, FakeCloudMethods.DeleteDirectory);
        cloud.IsInTrash(file.EntryId).Should().BeTrue();
        gateway.Resolve("\\directory").Should().BeNull();
    }

    [Fact]
    public void Cleanup_does_not_delete_nonempty_directory_without_delete_directory_callback()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile(cloud.AddDirectory("directory"), "file.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var directoryInfo = new FakeDokanFileInfo { IsDirectory = true, DeletePending = true };

        fileSystem.Cleanup("\\directory", directoryInfo);

        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.DeleteDirectory);
        fileSystem.LastSyncError.Should().Be(Loc.T("Eng_DirectoryDeleteFailedFmt",
            "directory", Loc.T("Eng_DirectoryNotEmpty")));
    }

    [Fact]
    public void Cleanup_keeps_directory_while_file_is_registered()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("directory");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        const string filePath = "\\directory\\fresh.txt";
        var fileInfo = new FakeDokanFileInfo();
        fileSystem.CreateFile(filePath, FileAccess.WriteData, FileShare.ReadWrite, FileMode.Create,
            FileOptions.None, FileAttributes.Normal, fileInfo).Should().Be(DokanResult.Success);
        fileSystem.WriteFile(filePath, [7, 8, 9], out var bytesWritten, 0, fileInfo).Should().Be(DokanResult.Success);
        bytesWritten.Should().Be(3);
        var directoryListings = cloud.Invoker.Calls.Count(call =>
            call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed
            && ((BarkCloud.Proto.Files.ListDirectoryRequest)call.Request).DirectoryId == directoryId);
        var directoryInfo = new FakeDokanFileInfo { IsDirectory = true, DeletePending = true };
        fileSystem.Cleanup("\\directory", directoryInfo);

        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.DeleteDirectory);
        cloud.Invoker.Calls.Count(call =>
            call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed
            && ((BarkCloud.Proto.Files.ListDirectoryRequest)call.Request).DirectoryId == directoryId)
            .Should().Be(directoryListings);
        fileSystem.LastSyncError.Should().Be(Loc.T("Eng_DirectoryDeleteFailedFmt",
            "directory", Loc.T("Eng_DirectoryNotEmpty")));

        fileSystem.Cleanup(filePath, fileInfo);
        fileSystem.CloseFile(filePath, fileInfo);
        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.DeleteDirectory);
    }

    [Fact]
    public void Cleanup_does_not_delete_directory_when_fresh_listing_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("directory");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        gateway.Resolve("\\directory").Should().NotBeNull();
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        cloud.Invoker.FailNext(FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "Сервер недоступен")));
        var directoryInfo = new FakeDokanFileInfo { IsDirectory = true, DeletePending = true };

        fileSystem.Cleanup("\\directory", directoryInfo);

        cloud.Invoker.Calls.Should().Contain(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed
            && ((BarkCloud.Proto.Files.ListDirectoryRequest)call.Request).DirectoryId == directoryId);
        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.DeleteDirectory);
        fileSystem.LastSyncError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DeleteDirectory_cleanup_does_not_delete_directory_when_file_flush_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("directory");
        var file = cloud.AddFile(directoryId, "file.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        fileSystem.SyncError = fileSystem.SetLastSyncError;
        var fileInfo = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\directory\\file.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, fileInfo).Should().Be(DokanResult.Success);
        fileSystem.DeleteFile("\\directory\\file.txt", fileInfo).Should().Be(DokanResult.Success);
        fileInfo.DeletePending = true;
        fileSystem.Cleanup("\\directory\\file.txt", fileInfo);
        fileSystem.CloseFile("\\directory\\file.txt", fileInfo);
        cloud.Invoker.FailNext(FakeCloudMethods.DeleteFileEntries,
            new RpcException(new Status(StatusCode.Unavailable, "Ошибка доставки удаления")));
        var directoryInfo = new FakeDokanFileInfo { IsDirectory = true };

        fileSystem.DeleteDirectory("\\directory", directoryInfo).Should().Be(DokanResult.Success);
        directoryInfo.DeletePending = true;
        fileSystem.Cleanup("\\directory", directoryInfo);

        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.DeleteDirectory);
        gateway.ListDirectory(directoryId).Files.Should().ContainSingle(item => item.Entry.Id == file.EntryId);
        fileSystem.LastSyncError.Should().Be(Loc.T("Eng_DirectoryDeleteFailedFmt",
            "directory", Loc.T("Eng_DirectoryNotEmpty")));
    }

    [Fact]
    public void DeleteFile_returns_access_denied_for_directory_and_file_not_found_for_missing_path()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddDirectory("directory");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        fileSystem.DeleteFile("\\directory", new FakeDokanFileInfo()).Should().Be(DokanResult.AccessDenied);
        fileSystem.DeleteFile("\\missing.txt", new FakeDokanFileInfo()).Should().Be(DokanResult.FileNotFound);
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
        cloud.Invoker.Calls.Should().NotContain(call =>
            call.MethodFullName == FakeCloudMethods.DeleteFileEntry
            || call.MethodFullName == FakeCloudMethods.RenameFileEntry
            || call.MethodFullName == FakeCloudMethods.MoveFileEntry
            || call.MethodFullName == FakeCloudMethods.DeleteFileEntries);
    }

    [Fact]
    public void MoveFile_uses_temporary_name_when_both_intermediate_names_are_occupied()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectory = cloud.AddDirectory("X");
        var destinationDirectory = cloud.AddDirectory("Y");
        var source = cloud.AddFile(sourceDirectory, "a.bin", [1]);
        cloud.AddFile(destinationDirectory, "a.bin", [2]);
        cloud.AddFile(sourceDirectory, "b.bin", [3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        var status = fileSystem.MoveFile("\\X\\a.bin", "\\Y\\b.bin", replace: false,
            new FakeDokanFileInfo());

        status.Should().Be(DokanResult.Success);
        gateway.Resolve("\\Y\\b.bin")!.EntryId.Should().Be(source.EntryId);
        gateway.Resolve("\\Y\\a.bin")!.Name.Should().Be("a.bin");
        gateway.Resolve("\\X\\b.bin")!.Name.Should().Be("b.bin");
        gateway.Resolve("\\X\\a.bin").Should().BeNull();
        cloud.Invoker.Calls.Where(call => call.MethodFullName is
                FakeCloudMethods.RenameFileEntry or FakeCloudMethods.MoveFileEntry)
            .Select(call => call.MethodFullName)
            .Should().Equal(FakeCloudMethods.RenameFileEntry, FakeCloudMethods.MoveFileEntry,
                FakeCloudMethods.RenameFileEntry);
    }

    [Fact]
    public void MoveFile_replaces_cloud_target_and_rolls_back_when_move_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectory = cloud.AddDirectory("X");
        var destinationDirectory = cloud.AddDirectory("Y");
        var source = cloud.AddFile(sourceDirectory, "a.bin", [1]);
        var target = cloud.AddFile(destinationDirectory, "b.bin", [2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        cloud.Invoker.FailNext(FakeCloudMethods.MoveFileEntry,
            new RpcException(new Status(StatusCode.Unavailable, "Сбой перемещения")));

        var status = fileSystem.MoveFile("\\X\\a.bin", "\\Y\\b.bin", replace: true,
            new FakeDokanFileInfo());

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        gateway.Resolve("\\X\\a.bin")!.EntryId.Should().Be(source.EntryId);
        gateway.Resolve("\\Y\\b.bin")!.EntryId.Should().Be(target.EntryId);
        fileSystem.LastSyncError.Should().BeNull();
        cloud.Invoker.Calls.Select(call => call.MethodFullName).Should().ContainInOrder(
            FakeCloudMethods.DeleteFileEntry, FakeCloudMethods.RenameFileEntry,
            FakeCloudMethods.MoveFileEntry, FakeCloudMethods.RenameFileEntry,
            FakeCloudMethods.RestoreFromTrash);
    }

    [Fact]
    public void MoveFile_deletes_cloud_target_before_renaming_cloud_source()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var source = cloud.AddFile("", "source.bin", [1]);
        var target = cloud.AddFile("", "target.bin", [2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        var status = fileSystem.MoveFile("\\source.bin", "\\target.bin", replace: true,
            new FakeDokanFileInfo());

        status.Should().Be(DokanResult.Success);
        var moved = gateway.Resolve("\\target.bin");
        moved!.EntryId.Should().Be(source.EntryId);
        moved.Name.Should().Be("target.bin");
        gateway.Resolve("\\source.bin").Should().BeNull();
        cloud.Invoker.Calls.Select(call => call.MethodFullName).Should().ContainInOrder(
            FakeCloudMethods.DeleteFileEntry, FakeCloudMethods.RenameFileEntry);
        target.EntryId.Should().NotBe(source.EntryId);
    }

    [Fact]
    public void MoveFile_replaces_cloud_target_on_persist_for_new_local_file()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var target = cloud.AddFile("", "target.bin", [9]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\source.bin", FileAccess.WriteData, FileShare.ReadWrite,
            FileMode.CreateNew, FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\source.bin", [1, 2, 3], out var written, 0, info)
            .Should().Be(DokanResult.Success);
        written.Should().Be(3);

        fileSystem.MoveFile("\\source.bin", "\\target.bin", replace: true,
            new FakeDokanFileInfo()).Should().Be(DokanResult.Success);

        gateway.Resolve("\\target.bin")!.EntryId.Should().Be(target.EntryId);
        cloud.Invoker.Calls.Should().NotContain(call =>
            call.MethodFullName == FakeCloudMethods.DeleteFileEntry
            || call.MethodFullName == FakeCloudMethods.RenameFileEntry
            || call.MethodFullName == FakeCloudMethods.MoveFileEntry);

        fileSystem.Cleanup("\\source.bin", info);
        fileSystem.CloseFile("\\target.bin", info);

        var replacement = gateway.Resolve("\\target.bin");
        replacement!.EntryId.Should().NotBe(target.EntryId);
        replacement.Name.Should().Be("target.bin");
        gateway.Resolve("\\source.bin").Should().BeNull();
        cloud.Invoker.Calls.Select(call => call.MethodFullName).Should().ContainInOrder(
            FakeCloudMethods.DeleteFileEntry, FakeCloudMethods.AttachFile);
    }

    [Fact]
    public void MoveFile_renames_cloud_file_when_only_case_changes()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var source = cloud.AddFile("", "name.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        var status = fileSystem.MoveFile("\\name.txt", "\\NAME.TXT", replace: false,
            new FakeDokanFileInfo());

        status.Should().Be(DokanResult.Success);
        gateway.Resolve("\\NAME.TXT")!.EntryId.Should().Be(source.EntryId);
        gateway.Resolve("\\NAME.TXT")!.Name.Should().Be("NAME.TXT");
        cloud.Invoker.Calls.Should().ContainSingle(call =>
            call.MethodFullName == FakeCloudMethods.RenameFileEntry);
    }

    [Fact]
    public void MoveFile_replaces_case_insensitive_cloud_target()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var target = cloud.AddFile("", "b.txt", [2]);
        var source = cloud.AddFile("", "a.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        fileSystem.MoveFile("\\a.txt", "\\B.txt", replace: false, new FakeDokanFileInfo())
            .Should().Be(DokanResult.FileExists);
        fileSystem.MoveFile("\\a.txt", "\\B.txt", replace: true, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        gateway.Resolve("\\B.txt")!.EntryId.Should().Be(source.EntryId);
        gateway.ListDirectory("").Files.Should().ContainSingle(file => file.Entry.Name == "B.txt");
        cloud.Invoker.Calls.Select(call => call.MethodFullName).Should().ContainInOrder(
            FakeCloudMethods.DeleteFileEntry, FakeCloudMethods.RenameFileEntry);
        target.EntryId.Should().NotBe(source.EntryId);
    }

    [Fact]
    public void MoveFile_returns_network_error_and_rolls_back_when_rename_response_is_lost()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var source = cloud.AddFile("", "source.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        cloud.Invoker.LoseNextResponseAfterEffect(FakeCloudMethods.RenameFileEntry);

        var status = fileSystem.MoveFile("\\source.txt", "\\moved.txt", replace: false,
            new FakeDokanFileInfo());

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        gateway.Resolve("\\source.txt")!.EntryId.Should().Be(source.EntryId);
        gateway.Resolve("\\moved.txt").Should().BeNull();
        fileSystem.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void MoveFile_rolls_back_after_move_response_is_lost()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectory = cloud.AddDirectory("X");
        cloud.AddDirectory("Y");
        var source = cloud.AddFile(sourceDirectory, "a.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        cloud.Invoker.LoseNextResponseAfterEffect(FakeCloudMethods.MoveFileEntry);

        var status = fileSystem.MoveFile("\\X\\a.txt", "\\Y\\b.txt", replace: false,
            new FakeDokanFileInfo());

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        gateway.Resolve("\\X\\a.txt")!.EntryId.Should().Be(source.EntryId);
        gateway.Resolve("\\Y\\b.txt").Should().BeNull();
        fileSystem.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void MoveFile_rolls_back_when_rename_applied_but_server_returns_internal()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var source = cloud.AddFile("", "source.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        cloud.Invoker.LoseNextResponseAfterEffect(FakeCloudMethods.RenameFileEntry, StatusCode.Internal);

        var status = fileSystem.MoveFile("\\source.txt", "\\renamed.txt", replace: false,
            new FakeDokanFileInfo());

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        gateway.Resolve("\\source.txt")!.EntryId.Should().Be(source.EntryId);
        gateway.Resolve("\\renamed.txt").Should().BeNull();
        fileSystem.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void MoveFile_restores_target_when_delete_response_is_lost()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var source = cloud.AddFile("", "source.txt", [1]);
        var target = cloud.AddFile("", "target.txt", [2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        cloud.Invoker.LoseNextResponseAfterEffect(FakeCloudMethods.DeleteFileEntry);

        var status = fileSystem.MoveFile("\\source.txt", "\\target.txt", replace: true,
            new FakeDokanFileInfo());

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        gateway.Resolve("\\source.txt")!.EntryId.Should().Be(source.EntryId);
        gateway.Resolve("\\target.txt")!.EntryId.Should().Be(target.EntryId);
        cloud.IsInTrash(target.EntryId).Should().BeFalse();
        fileSystem.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void MoveFile_does_not_report_already_restored_target_as_rollback_failure()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var source = cloud.AddFile("", "source.txt", [1]);
        var target = cloud.AddFile("", "target.txt", [2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        cloud.Invoker.FailNext(FakeCloudMethods.DeleteFileEntry,
            new RpcException(new Status(StatusCode.Unavailable, "Ответ на удаление потерян")));
        cloud.Invoker.FailNext(FakeCloudMethods.RestoreFromTrash,
            FakeCallInvoker.BusinessError(ServerErrors.FileEntryNotFound));

        var status = fileSystem.MoveFile("\\source.txt", "\\target.txt", replace: true,
            new FakeDokanFileInfo());

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        gateway.Resolve("\\source.txt")!.EntryId.Should().Be(source.EntryId);
        gateway.Resolve("\\target.txt")!.EntryId.Should().Be(target.EntryId);
        fileSystem.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void MoveFile_records_rollback_error_in_last_sync_error()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectory = cloud.AddDirectory("X");
        cloud.AddDirectory("Y");
        cloud.AddFile(sourceDirectory, "a.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var renameCalls = 0;
        cloud.Invoker.OnCall += method =>
        {
            if (method == FakeCloudMethods.RenameFileEntry && Interlocked.Increment(ref renameCalls) == 2)
                cloud.Invoker.FailNext(FakeCloudMethods.RenameFileEntry,
                    new RpcException(new Status(StatusCode.Unavailable, "Ошибка отката")));
        };
        cloud.Invoker.FailNext(FakeCloudMethods.MoveFileEntry,
            new RpcException(new Status(StatusCode.Unavailable, "Ошибка перемещения")));

        var status = fileSystem.MoveFile("\\X\\a.txt", "\\Y\\b.txt", replace: false,
            new FakeDokanFileInfo());

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        fileSystem.LastSyncError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void MoveFile_rolls_back_when_first_rename_of_three_step_move_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectory = cloud.AddDirectory("X");
        cloud.AddDirectory("Y");
        var source = cloud.AddFile(sourceDirectory, "a.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        cloud.Invoker.FailNext(FakeCloudMethods.RenameFileEntry,
            new RpcException(new Status(StatusCode.Unavailable, "Ошибка первого переименования")));

        var status = fileSystem.MoveFile("\\X\\a.txt", "\\Y\\b.txt", replace: false,
            new FakeDokanFileInfo());

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        gateway.Resolve("\\X\\a.txt")!.EntryId.Should().Be(source.EntryId);
        gateway.Resolve("\\Y\\b.txt").Should().BeNull();
        fileSystem.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void MoveFile_rolls_back_when_final_rename_of_three_step_move_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectory = cloud.AddDirectory("X");
        cloud.AddDirectory("Y");
        var source = cloud.AddFile(sourceDirectory, "a.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var renameCalls = 0;
        cloud.Invoker.OnCall += method =>
        {
            if (method == FakeCloudMethods.RenameFileEntry && Interlocked.Increment(ref renameCalls) == 2)
                cloud.Invoker.FailNext(FakeCloudMethods.RenameFileEntry,
                    new RpcException(new Status(StatusCode.Unavailable, "Ошибка последнего переименования")));
        };

        var status = fileSystem.MoveFile("\\X\\a.txt", "\\Y\\b.txt", replace: false,
            new FakeDokanFileInfo());

        status.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        gateway.Resolve("\\X\\a.txt")!.EntryId.Should().Be(source.EntryId);
        gateway.Resolve("\\Y\\b.txt").Should().BeNull();
        fileSystem.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void MoveFile_returns_access_denied_when_file_target_is_a_directory()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "source.txt", [1]);
        cloud.AddDirectory("target");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        fileSystem.MoveFile("\\source.txt", "\\TARGET", replace: true, new FakeDokanFileInfo())
            .Should().Be(DokanResult.AccessDenied);
        cloud.Invoker.Calls.Should().NotContain(call =>
            call.MethodFullName == FakeCloudMethods.DeleteFileEntry
            || call.MethodFullName == FakeCloudMethods.RenameFileEntry
            || call.MethodFullName == FakeCloudMethods.MoveFileEntry);
    }

    [Fact]
    public void MoveDirectory_renames_when_only_case_changes()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var source = cloud.AddDirectory("name");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        fileSystem.MoveFile("\\name", "\\NAME", replace: false,
            new FakeDokanFileInfo { IsDirectory = true }).Should().Be(DokanResult.Success);

        gateway.Resolve("\\NAME")!.DirectoryId.Should().Be(source);
        gateway.Resolve("\\NAME")!.Name.Should().Be("NAME");
        cloud.Invoker.Calls.Should().ContainSingle(call =>
            call.MethodFullName == FakeCloudMethods.RenameDirectory);
    }

    [Fact]
    public void MoveFile_updates_existing_writable_metadata_before_persisting_to_new_directory()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectory = cloud.AddDirectory("X");
        var destinationDirectory = cloud.AddDirectory("Y");
        var source = cloud.AddFile(sourceDirectory, "a.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\X\\a.txt", [7, 8]);

        fileSystem.MoveFile("\\X\\a.txt", "\\Y\\b.txt", replace: false,
            new FakeDokanFileInfo()).Should().Be(DokanResult.Success);

        local.Name.Should().Be("b.txt");
        local.DirectoryId.Should().Be(destinationDirectory);
        local.ExistingEntryId.Should().Be(source.EntryId);
        fileSystem.Cleanup("\\Y\\b.txt", info);
        fileSystem.CloseFile("\\Y\\b.txt", info);

        var moved = gateway.Resolve("\\Y\\b.txt");
        moved.Should().NotBeNull();
        moved!.DirectoryId.Should().Be(destinationDirectory);
        var bytes = new byte[2];
        gateway.Read(moved.FileId, moved.Length, bytes, 0).Should().Be(2);
        bytes.Should().Equal(7, 8);
        gateway.Resolve("\\X\\a.txt").Should().BeNull();
    }

    [Fact]
    public void MoveFile_flushes_tombstoned_target_before_safe_save_rename()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var target = cloud.AddFile("", "b.txt", [9]);
        var source = cloud.AddFile("", "a.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var deleteHandle = new FakeDokanFileInfo { DeletePending = true };
        fileSystem.CreateFile("\\b.txt", FileAccess.ReadData, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, deleteHandle).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\b.txt", deleteHandle);

        fileSystem.MoveFile("\\a.txt", "\\b.txt", replace: false, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        gateway.Resolve("\\b.txt")!.EntryId.Should().Be(source.EntryId);
        gateway.ListDirectory("").Files.Should().ContainSingle(file => file.Entry.Name == "b.txt");
        cloud.IsInTrash(target.EntryId).Should().BeTrue();
        fileSystem.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void MoveFile_returns_flush_error_without_rename_when_tombstoned_target_delete_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var target = cloud.AddFile("", "b.txt", [9]);
        var source = cloud.AddFile("", "a.txt", [1]);
        var time = new FakeTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var deleteHandle = new FakeDokanFileInfo { DeletePending = true };
        fileSystem.CreateFile("\\b.txt", FileAccess.ReadData, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, deleteHandle).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\b.txt", deleteHandle);
        cloud.Invoker.FailNext(FakeCloudMethods.DeleteFileEntries,
            new RpcException(new Status(StatusCode.Unavailable, "Ошибка доставки удаления")));

        fileSystem.MoveFile("\\a.txt", "\\b.txt", replace: false, new FakeDokanFileInfo())
            .Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        gateway.Resolve("\\b.txt").Should().BeNull();
        gateway.Resolve("\\a.txt")!.EntryId.Should().Be(source.EntryId);
        cloud.Invoker.Calls.Should().NotContain(call =>
            call.MethodFullName == FakeCloudMethods.DeleteFileEntry
            || call.MethodFullName == FakeCloudMethods.RenameFileEntry
            || call.MethodFullName == FakeCloudMethods.MoveFileEntry);

        time.Advance(TimeSpan.FromSeconds(1));

        cloud.IsInTrash(target.EntryId).Should().BeTrue();
        gateway.Resolve("\\b.txt").Should().BeNull();
        gateway.Resolve("\\a.txt")!.EntryId.Should().Be(source.EntryId);
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries).Should().Be(2);
    }

    [Fact]
    public void MoveFile_returns_file_exists_when_replace_targets_a_directory()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddDirectory("source");
        cloud.AddDirectory("target");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        var status = fileSystem.MoveFile("\\source", "\\target", replace: true,
            new FakeDokanFileInfo { IsDirectory = true });

        status.Should().Be(DokanResult.FileExists);
        cloud.Invoker.Calls.Should().NotContain(call =>
            call.MethodFullName == FakeCloudMethods.MoveDirectory
            || call.MethodFullName == FakeCloudMethods.RenameDirectory);
    }

    [Fact]
    public void MoveFile_denies_pending_source_and_target()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "source.bin", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        RegisterPending(fileSystem, "pending-source.bin", "");
        RegisterPending(fileSystem, "pending-target.bin", "");

        fileSystem.MoveFile("\\pending-source.bin", "\\renamed.bin", replace: true,
            new FakeDokanFileInfo()).Should().Be(DokanResult.AccessDenied);
        fileSystem.MoveFile("\\source.bin", "\\pending-target.bin", replace: true,
            new FakeDokanFileInfo()).Should().Be(DokanResult.AccessDenied);
        cloud.Invoker.Calls.Should().NotContain(call =>
            call.MethodFullName == FakeCloudMethods.DeleteFileEntry
            || call.MethodFullName == FakeCloudMethods.RenameFileEntry
            || call.MethodFullName == FakeCloudMethods.MoveFileEntry);
    }

    [Fact]
    public void MoveFile_returns_sharing_violation_for_open_local_target()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "source.bin", [1]);
        cloud.AddFile("", "target.bin", [2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var targetHandle = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\target.bin", FileAccess.WriteData, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, targetHandle).Should().Be(DokanResult.Success);

        var status = fileSystem.MoveFile("\\source.bin", "\\target.bin", replace: true,
            new FakeDokanFileInfo());

        status.Should().Be(DokanResult.SharingViolation);
        cloud.Invoker.Calls.Should().NotContain(call =>
            call.MethodFullName == FakeCloudMethods.DeleteFileEntry
            || call.MethodFullName == FakeCloudMethods.RenameFileEntry
            || call.MethodFullName == FakeCloudMethods.MoveFileEntry);
        fileSystem.Cleanup("\\target.bin", targetHandle);
        fileSystem.CloseFile("\\target.bin", targetHandle);
    }

    [Fact]
    public void MoveDirectory_updates_open_local_path_and_keeps_pending_key_by_directory_id()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("old");
        cloud.AddFile(directoryId, "open.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        RegisterPending(fileSystem, "pending.bin", directoryId);
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\old\\open.txt", FileAccess.WriteData, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var local = ((HandleContext)info.Context!).File!;
        fileSystem.WriteFile("\\old\\open.txt", [7], out _, 0, info).Should().Be(DokanResult.Success);

        var status = fileSystem.MoveFile("\\old", "\\new", replace: false,
            new FakeDokanFileInfo { IsDirectory = true });

        status.Should().Be(DokanResult.Success);
        AssertLocalFileAtPath(fileSystem, "\\new\\open.txt", local);
        local.DirectoryId.Should().Be(directoryId);
        fileSystem.MoveFile("\\new\\pending.bin", "\\new\\renamed.bin", replace: true,
            new FakeDokanFileInfo()).Should().Be(DokanResult.AccessDenied);

        fileSystem.Cleanup("\\new\\open.txt", info);
        fileSystem.CloseFile("\\new\\open.txt", info);

        gateway.Resolve("\\old").Should().BeNull();
        var persisted = gateway.Resolve("\\new\\open.txt");
        persisted.Should().NotBeNull();
        persisted!.DirectoryId.Should().Be(directoryId);
    }

    [Fact]
    public async Task MoveFile_returns_sharing_violation_without_rpc_when_source_persist_lock_is_held()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "source.bin", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\source.bin", [2]);

        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            Monitor.Enter(local.Sync);
            locked.Set();
            release.Wait();
            Monitor.Exit(local.Sync);
        });
        Task<NtStatus>? move = null;

        try
        {
            locked.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            var callCount = cloud.Invoker.Calls.Count;
            move = Task.Run(() => fileSystem.MoveFile("\\source.bin", "\\moved.bin", replace: false,
                new FakeDokanFileInfo()));
            (await move.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(DokanResult.SharingViolation);
            cloud.Invoker.Calls.Should().HaveCount(callCount);
        }
        finally
        {
            release.Set();
            await holder;
            if (move is not null)
                await move;
        }

        fileSystem.Cleanup("\\source.bin", info);
        fileSystem.CloseFile("\\source.bin", info);
        gateway.Resolve("\\source.bin").Should().NotBeNull();
    }

    [Fact]
    public async Task MoveDirectory_updates_nested_path_while_nested_sync_is_held()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("old");
        cloud.AddFile(directoryId, "open.txt", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\old\\open.txt", FileAccess.WriteData, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var local = ((HandleContext)info.Context!).File!;

        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            Monitor.Enter(local.Sync);
            locked.Set();
            release.Wait();
            Monitor.Exit(local.Sync);
        });
        Task<NtStatus>? move = null;
        try
        {
            (await Task.Run(() => locked.Wait(TimeSpan.FromSeconds(5)))).Should().BeTrue();
            move = Task.Run(() => fileSystem.MoveFile("\\old", "\\new", replace: false,
                new FakeDokanFileInfo { IsDirectory = true }));
            (await move.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(DokanResult.Success);
            AssertLocalFileAtPath(fileSystem, "\\new\\open.txt", local);
        }
        finally
        {
            release.Set();
            await holder;
            if (move is not null)
                await move;
        }

        fileSystem.Cleanup("\\new\\open.txt", info);
        fileSystem.CloseFile("\\new\\open.txt", info);
    }

    [Fact]
    public async Task MoveFile_returns_sharing_violation_when_closed_target_sync_is_held()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "source.bin", [1]);
        cloud.AddFile("", "target.bin", [2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (info, target) = OpenWritableAndWrite(fileSystem, "\\target.bin", [3]);
        fileSystem.Cleanup("\\target.bin", info);

        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            Monitor.Enter(target.Sync);
            locked.Set();
            release.Wait();
            Monitor.Exit(target.Sync);
        });
        Task? close = null;
        Task<NtStatus>? move = null;
        try
        {
            (await Task.Run(() => locked.Wait(TimeSpan.FromSeconds(5)))).Should().BeTrue();
            close = Task.Run(() => fileSystem.CloseFile("\\target.bin", info));
            SpinWait.SpinUntil(() => target.OpenCount == 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
            var callCount = cloud.Invoker.Calls.Count;

            move = Task.Run(() => fileSystem.MoveFile("\\source.bin", "\\target.bin", replace: true,
                new FakeDokanFileInfo()));
            (await move.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(DokanResult.SharingViolation);
            cloud.Invoker.Calls.Skip(callCount).Should().NotContain(call =>
                call.MethodFullName == FakeCloudMethods.DeleteFileEntry
                || call.MethodFullName == FakeCloudMethods.RenameFileEntry
                || call.MethodFullName == FakeCloudMethods.MoveFileEntry);
        }
        finally
        {
            release.Set();
            await holder;
            if (close is not null)
                await close;
            if (move is not null)
                await move;
        }
    }

    private static void RegisterPending(BarkCloudFileSystem fileSystem, string name, string directoryId)
    {
        fileSystem.RegisterOutboxPending(new UploadOutboxEntry
        {
            Id = Guid.NewGuid().ToString("D"),
            DataPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Name = name,
            DirectoryId = directoryId,
            ServerKey = "server",
            OwnerUserId = "42",
            Stage = PersistStage.Upload,
            CreatedAt = DateTimeOffset.UtcNow,
        });
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
    public async Task Reader_opened_before_writer_reads_hydrated_copy_while_upload_is_blocked()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "shared.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\shared.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\shared.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\shared.bin", [4, 5, 6], out _, 0, writer).Should().Be(DokanResult.Success);

        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = cloud.HttpHandler.Responder;
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            if (request.Method == System.Net.Http.HttpMethod.Post)
            {
                uploadStarted.TrySetResult();
                await releaseUpload.Task.WaitAsync(cancellationToken);
            }
            return await responder(request, cancellationToken);
        };

        var saving = Task.Run(() => fileSystem.Cleanup("\\shared.bin", writer));
        Task? reading = null;
        Exception? readError = null;
        try
        {
            await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reading = Task.Run(() =>
            {
                readStarted.TrySetResult();
                var buffer = new byte[3];
                fileSystem.ReadFile("\\shared.bin", buffer, out var bytesRead, 0, reader)
                    .Should().Be(DokanResult.Success);
                bytesRead.Should().Be(3);
                buffer.Should().Equal([4, 5, 6]);
            });
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await reading.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                readError = ex;
            }
        }
        finally
        {
            releaseUpload.TrySetResult();
            await saving;
            if (reading is not null)
            {
                try { await reading; }
                catch when (readError is not null) { }
            }
            fileSystem.Cleanup("\\shared.bin", reader);
            fileSystem.CloseFile("\\shared.bin", writer);
            fileSystem.CloseFile("\\shared.bin", reader);
        }
        readError.Should().BeNull("the reader should finish without waiting for the upload");
    }

    [Fact]
    public void Reader_opened_before_writer_reads_pending_copy_after_upload_failure()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "shared.bin", [1, 2, 3]);
        var responder = cloud.HttpHandler.Responder;
        cloud.HttpHandler.Responder = (request, cancellationToken) => request.Method == System.Net.Http.HttpMethod.Post
            ? Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError))
            : responder(request, cancellationToken);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\shared.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\shared.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        var local = ((HandleContext)writer.Context!).File!;
        fileSystem.WriteFile("\\shared.bin", [4, 5, 6], out _, 0, writer).Should().Be(DokanResult.Success);

        fileSystem.Cleanup("\\shared.bin", writer);
        fileSystem.CloseFile("\\shared.bin", writer);

        local.Kind.Should().Be(LocalFileKind.Pending);
        local.OpenCount.Should().Be(0);
        var buffer = new byte[3];
        fileSystem.ReadFile("\\shared.bin", buffer, out var bytesRead, 0, reader).Should().Be(DokanResult.Success);
        bytesRead.Should().Be(3);
        buffer.Should().Equal([4, 5, 6]);

        fileSystem.Cleanup("\\shared.bin", reader);
        fileSystem.CloseFile("\\shared.bin", reader);
    }

    [Fact]
    public void Opening_existing_file_for_write_without_changes_does_not_download_or_upload()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "existing.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox,
            "server", () => "42");

        foreach (var (mode, expectedStatus) in new[]
                 {
                     (FileMode.Open, DokanResult.Success),
                     (FileMode.OpenOrCreate, DokanResult.AlreadyExists),
                 })
        {
            var info = new FakeDokanFileInfo();
            fileSystem.CreateFile("\\existing.bin", FileAccess.WriteData, FileShare.ReadWrite, mode,
                FileOptions.None, FileAttributes.Normal, info).Should().Be(expectedStatus);
            fileSystem.Cleanup("\\existing.bin", info);
            fileSystem.CloseFile("\\existing.bin", info);
        }

        cloud.HttpHandler.Calls.Should().BeEmpty();
        outbox.Count.Should().Be(0);
    }

    [Fact]
    public void GetFileInformation_before_hydration_uses_cloud_length_without_download()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "existing.bin", [1, 2, 3, 4]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\existing.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);

        fileSystem.GetFileInformation("\\existing.bin", out var info, writer).Should().Be(DokanResult.Success);
        info.Length.Should().Be(4);
        fileSystem.FindFiles("\\", out var files, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        files.Single(file => file.FileName == "existing.bin").Length.Should().Be(4);
        cloud.HttpHandler.Calls.Should().BeEmpty();

        fileSystem.Cleanup("\\existing.bin", writer);
        fileSystem.CloseFile("\\existing.bin", writer);
    }

    [Theory]
    [InlineData(FileMode.Create, DokanResult.AlreadyExists)]
    [InlineData(FileMode.Truncate, DokanResult.Success)]
    public void Create_or_truncate_existing_file_resets_copy_without_downloading(
        FileMode mode, NtStatus expectedStatus)
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "truncate.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();

        fileSystem.CreateFile("\\truncate.bin", FileAccess.WriteData, FileShare.ReadWrite, mode,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(expectedStatus);
        var local = ((HandleContext)writer.Context!).File!;
        local.Hydrated.Should().BeTrue();
        fileSystem.GetFileInformation("\\truncate.bin", out var info, writer).Should().Be(DokanResult.Success);
        info.Length.Should().Be(0);
        cloud.HttpHandler.Calls.Should().BeEmpty();

        fileSystem.Cleanup("\\truncate.bin", writer);
        fileSystem.CloseFile("\\truncate.bin", writer);
    }

    [Fact]
    public async Task Writing_in_the_middle_hydrates_and_uploads_the_full_contents()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "existing.bin", [1, 2, 3, 4, 5]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\existing.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Should().BeEmpty();

        fileSystem.WriteFile("\\existing.bin", [8, 9], out var bytesWritten, 2, writer)
            .Should().Be(DokanResult.Success);
        bytesWritten.Should().Be(2);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);

        fileSystem.Cleanup("\\existing.bin", writer);
        fileSystem.CloseFile("\\existing.bin", writer);
        cloud.HttpHandler.Calls.Count(call => call.Method == "POST").Should().Be(1);

        var saved = gateway.ListDirectory("").Files.Single(file => file.Entry.Name == "existing.bin");
        var savedPath = temp.Path("saved.bin");
        await gateway.DownloadToAsync(saved.File.Id, savedPath, saved.File.FileSize);
        File.ReadAllBytes(savedPath).Should().Equal([1, 2, 8, 9, 5]);
    }

    [Fact]
    public async Task Full_overwrite_from_zero_does_not_download_the_cloud_file()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "replace.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\replace.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);

        fileSystem.WriteFile("\\replace.bin", [7, 8, 9], out _, 0, writer).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(0);
        fileSystem.Cleanup("\\replace.bin", writer);
        fileSystem.CloseFile("\\replace.bin", writer);
        cloud.HttpHandler.Calls.Count(call => call.Method == "POST").Should().Be(1);

        var saved = gateway.ListDirectory("").Files.Single(file => file.Entry.Name == "replace.bin");
        var savedPath = temp.Path("saved.bin");
        await gateway.DownloadToAsync(saved.File.Id, savedPath, saved.File.FileSize);
        File.ReadAllBytes(savedPath).Should().Equal([7, 8, 9]);
    }

    [Fact]
    public async Task Short_write_from_zero_hydrates_and_preserves_the_tail()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "short.bin", [1, 2, 3, 4]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\short.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);

        fileSystem.WriteFile("\\short.bin", [9], out _, 0, writer).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);
        fileSystem.Cleanup("\\short.bin", writer);
        fileSystem.CloseFile("\\short.bin", writer);

        var saved = gateway.ListDirectory("").Files.Single(file => file.Entry.Name == "short.bin");
        var savedPath = temp.Path("saved.bin");
        await gateway.DownloadToAsync(saved.File.Id, savedPath, saved.File.FileSize);
        File.ReadAllBytes(savedPath).Should().Equal([9, 2, 3, 4]);
    }

    [Fact]
    public void SetEndOfFile_zero_truncates_without_downloading()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "zero.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\zero.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);

        fileSystem.SetEndOfFile("\\zero.bin", 0, writer).Should().Be(DokanResult.Success);
        fileSystem.GetFileInformation("\\zero.bin", out var info, writer).Should().Be(DokanResult.Success);
        info.Length.Should().Be(0);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(0);
        fileSystem.Cleanup("\\zero.bin", writer);
        fileSystem.CloseFile("\\zero.bin", writer);
        cloud.HttpHandler.Calls.Count(call => call.Method == "POST").Should().Be(0);
    }

    [Fact]
    public async Task SetEndOfFile_in_the_middle_hydrates_and_uploads_the_truncated_contents()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "truncate.bin", [1, 2, 3, 4]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\truncate.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);

        fileSystem.SetEndOfFile("\\truncate.bin", 2, writer).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);
        fileSystem.Cleanup("\\truncate.bin", writer);
        fileSystem.CloseFile("\\truncate.bin", writer);

        var saved = gateway.ListDirectory("").Files.Single(file => file.Entry.Name == "truncate.bin");
        var savedPath = temp.Path("saved.bin");
        await gateway.DownloadToAsync(saved.File.Id, savedPath, saved.File.FileSize);
        File.ReadAllBytes(savedPath).Should().Equal([1, 2]);
    }

    [Fact]
    public void SetAllocationSize_hydrates_for_nonzero_size_and_truncates_zero_without_download()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "allocation-zero.bin", [1, 2, 3]);
        cloud.AddFile("", "allocation.bin", [4, 5, 6, 7]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var zero = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\allocation-zero.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, zero).Should().Be(DokanResult.Success);
        fileSystem.SetAllocationSize("\\allocation-zero.bin", 0, zero).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(0);
        fileSystem.Cleanup("\\allocation-zero.bin", zero);
        fileSystem.CloseFile("\\allocation-zero.bin", zero);

        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\allocation.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        fileSystem.SetAllocationSize("\\allocation.bin", 2, writer).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);
        fileSystem.Cleanup("\\allocation.bin", writer);
        fileSystem.CloseFile("\\allocation.bin", writer);
    }

    [Fact]
    public void ReadFile_on_writer_handle_hydrates_and_returns_cloud_contents()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "read.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\read.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        var local = ((HandleContext)writer.Context!).File!;
        local.Hydrated.Should().BeFalse();

        var buffer = new byte[3];
        fileSystem.ReadFile("\\read.bin", buffer, out var bytesRead, 0, writer).Should().Be(DokanResult.Success);
        bytesRead.Should().Be(3);
        buffer.Should().Equal([1, 2, 3]);
        local.Hydrated.Should().BeTrue();
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);
        fileSystem.Cleanup("\\read.bin", writer);
        fileSystem.CloseFile("\\read.bin", writer);
    }

    [Fact]
    public void Reader_opened_before_writer_hydrates_local_copy_through_entry_registry()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "reader.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\reader.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\reader.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        var local = ((HandleContext)writer.Context!).File!;
        local.Hydrated.Should().BeFalse();

        var buffer = new byte[3];
        fileSystem.ReadFile("\\reader.bin", buffer, out var bytesRead, 0, reader).Should().Be(DokanResult.Success);
        bytesRead.Should().Be(3);
        buffer.Should().Equal([1, 2, 3]);
        local.Hydrated.Should().BeTrue();
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);

        fileSystem.Cleanup("\\reader.bin", writer);
        fileSystem.Cleanup("\\reader.bin", reader);
        fileSystem.CloseFile("\\reader.bin", writer);
        fileSystem.CloseFile("\\reader.bin", reader);
    }

    [Fact]
    public async Task Reader_handle_falls_back_to_cloud_if_local_copy_closes_before_hydration()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "reader-close.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\reader-close.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\reader-close.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        var local = ((HandleContext)writer.Context!).File!;
        var localFileFound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fileSystem.ReaderLocalFileFoundForTesting = () => localFileFound.TrySetResult();
        var buffer = new byte[3];
        var holdSyncAndCloseWriter = Task.Run(() =>
        {
            Monitor.Enter(local.Sync);
            try
            {
                syncHeld.TrySetResult();
                localFileFound.Task.GetAwaiter().GetResult();
                fileSystem.CloseFile("\\reader-close.bin", writer);
            }
            finally
            {
                Monitor.Exit(local.Sync);
            }
        });
        Task<(NtStatus Status, int BytesRead)>? read = null;
        try
        {
            await syncHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            read = Task.Run(() =>
            {
                var status = fileSystem.ReadFile("\\reader-close.bin", buffer, out var bytesRead, 0, reader);
                return (status, bytesRead);
            });
            await localFileFound.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await holdSyncAndCloseWriter.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            localFileFound.TrySetResult();
            await holdSyncAndCloseWriter.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var result = await read!.WaitAsync(TimeSpan.FromSeconds(5));
        result.Status.Should().Be(DokanResult.Success);
        result.BytesRead.Should().Be(3);
        buffer.Should().Equal([1, 2, 3]);
        local.Hydrated.Should().BeFalse();
        local.Stream.Should().BeNull();
        File.Exists(local.TempPath).Should().BeFalse();
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);

        fileSystem.Cleanup("\\reader-close.bin", reader);
        fileSystem.CloseFile("\\reader-close.bin", reader);
    }

    [Fact]
    public async Task Concurrent_reads_share_one_hydration()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "concurrent.bin", [1, 2, 3]);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalResponder = cloud.HttpHandler.Responder;
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                requestStarted.TrySetResult();
                await releaseDownload.Task.WaitAsync(cancellationToken);
            }
            return await originalResponder(request, cancellationToken);
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\concurrent.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);

        Task<(NtStatus Status, int BytesRead, byte[] Buffer)> ReadAsync()
            => Task.Run(() =>
            {
                var buffer = new byte[3];
                var status = fileSystem.ReadFile("\\concurrent.bin", buffer, out var bytesRead, 0, writer);
                return (status, bytesRead, buffer);
            });

        var firstRead = ReadAsync();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRead = ReadAsync();
        releaseDownload.TrySetResult();
        var results = await Task.WhenAll(firstRead, secondRead);

        results.Should().OnlyContain(result => result.Status == DokanResult.Success && result.BytesRead == 3);
        results.Should().OnlyContain(result => result.Buffer.SequenceEqual(new byte[] { 1, 2, 3 }));
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);
        fileSystem.Cleanup("\\concurrent.bin", writer);
        fileSystem.CloseFile("\\concurrent.bin", writer);
    }

    [Fact]
    public async Task Writing_past_end_hydrates_and_uploads_zero_filled_gap()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "sparse.bin", [1, 2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\sparse.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);

        fileSystem.WriteFile("\\sparse.bin", [9], out _, 5, writer).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);
        fileSystem.Cleanup("\\sparse.bin", writer);
        fileSystem.CloseFile("\\sparse.bin", writer);

        var saved = gateway.ListDirectory("").Files.Single(file => file.Entry.Name == "sparse.bin");
        var savedPath = temp.Path("saved.bin");
        await gateway.DownloadToAsync(saved.File.Id, savedPath, saved.File.FileSize);
        File.ReadAllBytes(savedPath).Should().Equal([1, 2, 0, 0, 0, 9]);
    }

    [Fact]
    public async Task SetEndOfFile_increase_hydrates_and_uploads_zero_filled_tail()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "extend.bin", [1, 2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\extend.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);

        fileSystem.SetEndOfFile("\\extend.bin", 5, writer).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);
        fileSystem.Cleanup("\\extend.bin", writer);
        fileSystem.CloseFile("\\extend.bin", writer);

        var saved = gateway.ListDirectory("").Files.Single(file => file.Entry.Name == "extend.bin");
        var savedPath = temp.Path("saved.bin");
        await gateway.DownloadToAsync(saved.File.Id, savedPath, saved.File.FileSize);
        File.ReadAllBytes(savedPath).Should().Equal([1, 2, 0, 0, 0]);
    }

    [Fact]
    public void SetEndOfFile_to_current_length_does_not_hydrate_or_modify()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "same-length.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\same-length.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        var local = ((HandleContext)writer.Context!).File!;

        fileSystem.SetEndOfFile("\\same-length.bin", 3, writer).Should().Be(DokanResult.Success);

        local.Hydrated.Should().BeFalse();
        local.Modified.Should().BeFalse();
        cloud.HttpHandler.Calls.Should().BeEmpty();
        fileSystem.Cleanup("\\same-length.bin", writer);
        fileSystem.CloseFile("\\same-length.bin", writer);
    }

    [Fact]
    public void SetAllocationSize_does_not_hydrate_when_length_is_not_smaller()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "allocation-noop.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\allocation-noop.bin", FileAccess.WriteData, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        var local = ((HandleContext)writer.Context!).File!;

        fileSystem.SetAllocationSize("\\allocation-noop.bin", 3, writer).Should().Be(DokanResult.Success);
        fileSystem.SetAllocationSize("\\allocation-noop.bin", 5, writer).Should().Be(DokanResult.Success);

        local.Hydrated.Should().BeFalse();
        cloud.HttpHandler.Calls.Should().BeEmpty();
        fileSystem.Cleanup("\\allocation-noop.bin", writer);
        fileSystem.CloseFile("\\allocation-noop.bin", writer);
    }

    [Fact]
    public async Task Writing_after_moving_unhydrated_file_hydrates_by_file_id_and_uploads_under_new_name()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "before-move.bin", [1, 2, 3, 4]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\before-move.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        var local = ((HandleContext)writer.Context!).File!;
        var originalFileId = local.ExistingNode!.FileId;

        fileSystem.MoveFile("\\before-move.bin", "\\after-move.bin", replace: false,
            new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        local.Hydrated.Should().BeFalse();
        fileSystem.WriteFile("\\after-move.bin", [9], out _, 2, writer).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);
        local.ExistingNode!.FileId.Should().Be(originalFileId);
        fileSystem.Cleanup("\\after-move.bin", writer);
        fileSystem.CloseFile("\\after-move.bin", writer);

        gateway.Resolve("\\before-move.bin").Should().BeNull();
        var saved = gateway.ListDirectory("").Files.Should().ContainSingle(file => file.Entry.Name == "after-move.bin").Subject;
        var savedPath = temp.Path("saved.bin");
        await gateway.DownloadToAsync(saved.File.Id, savedPath, saved.File.FileSize);
        File.ReadAllBytes(savedPath).Should().Equal([1, 2, 9, 4]);
    }

    [Fact]
    public async Task Two_writer_handles_share_one_copy_for_full_and_partial_writes()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "shared-overwrite.bin", [1, 2, 3, 4]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var fullWriter = new FakeDokanFileInfo();
        var partialWriter = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\shared-overwrite.bin", FileAccess.WriteData, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, fullWriter).Should().Be(DokanResult.Success);
        fileSystem.CreateFile("\\shared-overwrite.bin", FileAccess.WriteData, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, partialWriter).Should().Be(DokanResult.Success);

        fileSystem.WriteFile("\\shared-overwrite.bin", [7, 8, 9, 10], out _, 0, fullWriter)
            .Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\shared-overwrite.bin", [5], out _, 1, partialWriter)
            .Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Should().BeEmpty();
        fileSystem.Cleanup("\\shared-overwrite.bin", fullWriter);
        fileSystem.Cleanup("\\shared-overwrite.bin", partialWriter);
        fileSystem.CloseFile("\\shared-overwrite.bin", fullWriter);
        fileSystem.CloseFile("\\shared-overwrite.bin", partialWriter);

        var saved = gateway.ListDirectory("").Files.Single(file => file.Entry.Name == "shared-overwrite.bin");
        var savedPath = temp.Path("saved.bin");
        await gateway.DownloadToAsync(saved.File.Id, savedPath, saved.File.FileSize);
        File.ReadAllBytes(savedPath).Should().Equal([7, 5, 9, 10]);
    }

    [Fact]
    public async Task Write_to_end_of_file_hydrates_and_appends_even_when_paging_io_is_set()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "append.bin", [1, 2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo { PagingIo = true, WriteToEndOfFile = true };
        fileSystem.CreateFile("\\append.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);

        fileSystem.WriteFile("\\append.bin", [3], out _, 0, writer).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Calls.Count(call => call.Method == "GET").Should().Be(1);
        fileSystem.Cleanup("\\append.bin", writer);
        fileSystem.CloseFile("\\append.bin", writer);

        var saved = gateway.ListDirectory("").Files.Single(file => file.Entry.Name == "append.bin");
        var savedPath = temp.Path("saved.bin");
        await gateway.DownloadToAsync(saved.File.Id, savedPath, saved.File.FileSize);
        File.ReadAllBytes(savedPath).Should().Equal([1, 2, 3]);
    }

    [Fact]
    public void Failed_hydration_keeps_local_file_unhydrated_and_can_be_retried()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "retry-read.bin", [1, 2, 3]);
        var originalResponder = cloud.HttpHandler.Responder;
        var failNextDownload = true;
        cloud.HttpHandler.Responder = (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get && failNextDownload)
            {
                failNextDownload = false;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            }
            return originalResponder(request, cancellationToken);
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var writer = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\retry-read.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        var local = ((HandleContext)writer.Context!).File!;
        var buffer = new byte[3];

        fileSystem.ReadFile("\\retry-read.bin", buffer, out var firstRead, 0, writer)
            .Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        firstRead.Should().Be(0);
        local.Hydrated.Should().BeFalse();
        fileSystem.ReadFile("\\retry-read.bin", buffer, out var secondRead, 0, writer)
            .Should().Be(DokanResult.Success);
        secondRead.Should().Be(3);
        buffer.Should().Equal([1, 2, 3]);
        local.Hydrated.Should().BeTrue();

        fileSystem.Cleanup("\\retry-read.bin", writer);
        fileSystem.CloseFile("\\retry-read.bin", writer);
    }

    [Fact]
    public void Read_handle_uses_opened_node_after_listing_cache_expires()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "cached.bin", [1, 2, 3]);
        var time = new FakeTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var reader = new FakeDokanFileInfo();

        fileSystem.CreateFile("\\cached.bin", FileAccess.ReadData | FileAccess.GenericRead, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        var handle = (HandleContext)reader.Context!;
        handle.Node.Should().NotBeNull();
        handle.File.Should().BeNull();
        var listingCalls = cloud.Invoker.Calls.Count(call =>
            call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed);
        listingCalls.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(6));
        var buffer = new byte[3];
        for (var i = 0; i < 100; i++)
        {
            fileSystem.ReadFile("\\cached.bin", buffer, out var bytesRead, 0, reader).Should()
                .Be(DokanResult.Success);
            bytesRead.Should().Be(3);
            buffer.Should().Equal([1, 2, 3]);

            fileSystem.GetFileInformation("\\cached.bin", out var info, reader).Should()
                .Be(DokanResult.Success);
            info.Length.Should().Be(3);
        }

        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(listingCalls);
        fileSystem.Cleanup("\\cached.bin", reader);
        fileSystem.CloseFile("\\cached.bin", reader);
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
    public async Task Stopping_outbox_during_restore_does_not_cancel_compensating_restore()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var original = cloud.AddFile("", "restore-on-stop.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox,
            "server", () => "owner");
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\restore-on-stop.bin", [4, 5, 6]);
        var responder = cloud.HttpHandler.Responder;
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        fileSystem.Cleanup("\\restore-on-stop.bin", info);
        fileSystem.CloseFile("\\restore-on-stop.bin", info);
        cloud.HttpHandler.Responder = responder;
        local.OutboxId.Should().NotBeNull();

        cloud.Invoker.FailNext(FakeCloudMethods.AttachFile,
            FakeCallInvoker.BusinessError(ServerErrors.DirectoryNotFound));
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.Invoker.OnCall = method =>
        {
            if (method != FakeCloudMethods.RestoreFromTrash)
                return;
            restoreStarted.TrySetResult();
            releaseRestore.Task.GetAwaiter().GetResult();
        };
        var stopObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new UploadOutboxWorker(outbox,
            () => ("server", "owner"),
            (entry, cancellationToken) =>
            {
                cancellationToken.Register(() => stopObserved.TrySetResult());
                return fileSystem.ProcessOutboxEntryAsync(entry, cancellationToken);
            },
            fileSystem.CompleteOutboxEntry,
            time: new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        worker.Start();

        var pass = Task.Factory.StartNew(() => worker.TriggerAsync().GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await restoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = Task.Factory.StartNew(() => worker.StopAsync().GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await stopObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseRestore.TrySetResult();
        await Task.WhenAll(pass, stop).WaitAsync(TimeSpan.FromSeconds(5));

        cloud.IsInTrash(original.EntryId).Should().BeFalse();
        outbox.Entries.Single().Op!.OldEntryDeleted.Should().BeFalse();
        cloud.Invoker.Calls.Single(call => call.MethodFullName == FakeCloudMethods.RestoreFromTrash)
            .CancellationToken.CanBeCanceled.Should().BeFalse();
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
        await gateway.DownloadToAsync(saved.File.Id, downloaded, 3);
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
        await gateway.DownloadToAsync(saved.File.Id, temp.Path("saved.bin"), 3);
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
        await gateway.DownloadToAsync(saved.File.Id, temp.Path("saved.bin"), 3);
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
        await gateway.DownloadToAsync(saved.File.Id, temp.Path("saved.bin"), 3);
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
        var first = new FakeDokanFileInfo();
        var second = new FakeDokanFileInfo();

        var openings = await Task.WhenAll(
            Task.Run(() => fs.CreateFile("\\race.bin", FileAccess.WriteData, FileShare.ReadWrite,
                mode, FileOptions.None, FileAttributes.Normal, first)),
            Task.Run(() => fs.CreateFile("\\race.bin", FileAccess.WriteData, FileShare.ReadWrite,
                mode, FileOptions.None, FileAttributes.Normal, second)));

        openings.Should().OnlyContain(status => status == DokanResult.Success);
        ((HandleContext)first.Context!).File.Should().BeSameAs(((HandleContext)second.Context!).File);
        cloud.HttpHandler.Calls.Should().BeEmpty();
        fs.Cleanup("\\race.bin", first);
        fs.Cleanup("\\race.bin", second);
        fs.CloseFile("\\race.bin", first);
        fs.CloseFile("\\race.bin", second);
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

    private static void AssertLocalFileAtPath(BarkCloudFileSystem fileSystem, string path, LocalFile expected)
    {
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile(path, FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        ((HandleContext)info.Context!).File.Should().BeSameAs(expected);
        fileSystem.CloseFile(path, info);
    }
}
