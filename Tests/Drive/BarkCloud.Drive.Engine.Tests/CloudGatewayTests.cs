using BarkCloud.Drive.Engine;
using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using FluentAssertions;

using Grpc.Core;

using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class CloudGatewayTests
{
    [Fact]
    public void Resolve_finds_files_case_insensitively()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("Documents");
        var file = cloud.AddFile(directoryId, "report.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var resolved = gateway.Resolve("\\documents\\REPORT.txt");

        resolved.Should().NotBeNull();
        resolved!.EntryId.Should().Be(file.EntryId);
        resolved.FileId.Should().Be(file.FileId);
        resolved.DirectoryId.Should().Be(directoryId);
    }

    [Fact]
    public void ListDirectory_fetches_all_pages()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        for (var i = 0; i < 205; i++)
            cloud.AddFile("", $"file-{i:D3}.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var listing = gateway.ListDirectory("");

        listing.Files.Should().HaveCount(205);
        var calls = cloud.Invoker.Calls
            .Where(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .ToArray();
        calls.Should().HaveCount(2);
        calls.Select(call => ((ListDirectoryRequest)call.Request).Limit).Should().OnlyContain(limit => limit == 200);
        ((ListDirectoryRequest)calls[1].Request).CursorEntryId.Should().NotBeEmpty();
    }

    [Fact]
    public void ListDirectory_reuses_entries_until_ttl_expires()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "first.txt");
        var time = new FakeTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        gateway.ListDirectory("").Files.Should().HaveCount(1);
        cloud.AddFile("", "second.txt");

        time.Advance(TimeSpan.FromSeconds(4));
        gateway.ListDirectory("").Files.Should().HaveCount(1);
        time.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(1));
        gateway.ListDirectory("").Files.Should().HaveCount(2);

        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(2);
    }

    [Fact]
    public void Buffered_delete_hides_entries_and_sends_a_batch_of_100()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var files = Enumerable.Range(0, 100).Select(i => cloud.AddFile("", $"file-{i:D3}.txt")).ToArray();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        foreach (var file in files.Take(99))
            gateway.QueueDeleteFileEntry(file.EntryId, "");
        gateway.ListDirectory("").Files.Should().ContainSingle();

        gateway.QueueDeleteFileEntry(files[99].EntryId, "");

        var deleteCalls = cloud.Invoker.Calls
            .Where(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries)
            .ToArray();
        deleteCalls.Should().ContainSingle();
        ((DeleteFileEntriesRequest)deleteCalls[0].Request).EntryIds.Should().HaveCount(100);
        deleteCalls[0].Deadline.Should().Be(time.GetUtcNow().UtcDateTime.AddSeconds(5));
        gateway.ListDirectory("").Files.Should().BeEmpty();
    }

    [Fact]
    public void Buffered_delete_sends_a_batch_after_the_quiet_window()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var files = new[] { cloud.AddFile("", "first.txt"), cloud.AddFile("", "second.txt") };
        var time = new FakeTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        foreach (var file in files)
            gateway.QueueDeleteFileEntry(file.EntryId, "");

        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries);
        time.Advance(TimeSpan.FromSeconds(1));

        var deleteCall = cloud.Invoker.Calls.Should()
            .ContainSingle(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries)
            .Subject;
        ((DeleteFileEntriesRequest)deleteCall.Request).EntryIds.Should().HaveCount(2);
    }

    [Fact]
    public void FlushPending_returns_hidden_entries_when_delete_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var file = cloud.AddFile("", "pending-delete.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        gateway.QueueDeleteFileEntry(file.EntryId, "");
        cloud.Invoker.FailNext(
            FakeCloudMethods.DeleteFileEntries,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));

        gateway.FlushPending();

        gateway.ListDirectory("").Files.Should().ContainSingle(item => item.Entry.Id == file.EntryId);
    }

    [Fact]
    public void Resolve_returns_root_directories_and_null_for_missing_paths()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("Documents");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var root = gateway.Resolve("\\");
        root.Should().NotBeNull();
        root!.IsDirectory.Should().BeTrue();
        root.DirectoryId.Should().BeEmpty();

        var directory = gateway.Resolve("\\Documents");
        directory.Should().NotBeNull();
        directory!.IsDirectory.Should().BeTrue();
        directory.DirectoryId.Should().Be(directoryId);
        gateway.Resolve("\\missing").Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_null_when_a_file_is_in_the_middle_of_the_path()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "file.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        gateway.Resolve("\\file.txt\\child.txt").Should().BeNull();
    }

    [Fact]
    public void Resolve_and_ListDirectory_propagate_listing_rpc_errors()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        cloud.Invoker.FailNext(
            FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));

        var resolve = () => gateway.Resolve("\\missing");

        resolve.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.Unavailable);

        cloud.Invoker.FailNext(
            FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));
        var list = () => gateway.ListDirectory("");

        list.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.Unavailable);
    }

    [Fact]
    public void FakeCloud_clamps_directory_listing_limit_to_the_backend_maximum()
    {
        var cloud = new FakeCloud();
        for (var i = 0; i < 205; i++)
            cloud.AddFile("", $"file-{i:D3}.txt");
        var client = new CloudApi.CloudApiClient(cloud.Invoker);

        var page = client.ListDirectoryDetailed(new ListDirectoryRequest { Limit = 500 });

        page.Files.Should().HaveCount(200);
        page.NextCursorEntryId.Should().NotBeEmpty();
    }

    [Fact]
    public void FakeCloud_uses_separate_case_sensitive_file_and_directory_names()
    {
        var cloud = new FakeCloud();
        var client = new CloudApi.CloudApiClient(cloud.Invoker);
        var file = cloud.AddFile("", "item.txt");
        var directory = cloud.AddDirectory("item.txt");
        cloud.AddFile("", "ITEM.txt");
        cloud.AddDirectory("ITEM.txt");

        client.RenameFileEntry(new RenameFileEntryRequest { EntryId = file.EntryId, NewName = "Item.txt" });
        client.RenameDirectory(new RenameDirectoryRequest { DirectoryId = directory, NewName = "Item.txt" });
    }

    [Fact]
    public void FakeCloud_attach_and_restore_suffix_only_for_file_name_conflicts()
    {
        var cloud = new FakeCloud();
        cloud.AddDirectory("same.txt");
        var fileId = cloud.AddUnattachedFile();
        var client = new CloudApi.CloudApiClient(cloud.Invoker);

        client.AttachFile(new AttachFileRequest { DirectoryId = "", FileId = fileId, Name = "same.txt" });
        var attached = client.ListDirectoryDetailed(new ListDirectoryRequest { DirectoryId = "", Limit = 50 })
            .Files.Should().ContainSingle().Which;
        attached.Entry.Name.Should().Be("same.txt");

        client.DeleteFileEntry(new DeleteFileEntryRequest { EntryId = attached.Entry.Id });
        client.RestoreFromTrash(new RestoreFromTrashRequest { EntryId = attached.Entry.Id });

        client.ListDirectoryDetailed(new ListDirectoryRequest { DirectoryId = "", Limit = 50 })
            .Files.Should().ContainSingle().Which.Entry.Name.Should().Be("same.txt");
    }

    [Fact]
    public void FakeCloud_reports_circular_directory_moves_with_the_backend_error_code()
    {
        var cloud = new FakeCloud();
        var parent = cloud.AddDirectory("parent");
        var child = cloud.AddDirectory("child", parent);
        var client = new CloudApi.CloudApiClient(cloud.Invoker);

        var exception = Assert.Throws<RpcException>(() => client.MoveDirectory(
            new MoveDirectoryRequest { DirectoryId = parent, NewParentId = child }));

        exception.Status.Detail.Should().Be("Операция отклонена сервером");
        exception.Trailers.GetValue("x-error-code").Should().Be("F2B82A3C-1D2E-4B0E-9A19-2D90F8B7C001");
    }

    [Fact]
    public async Task CloudGateway_uploads_content_and_reads_ranges()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var content = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var upload = gateway.GetUploadUrl();
        var uploadPath = temp.Path("upload.bin");
        File.WriteAllBytes(uploadPath, content);

        var fileId = await gateway.UploadAsync(upload.Url, "upload.bin", uploadPath);
        gateway.AttachFile("", fileId, "upload.bin");
        var buffer = new byte[4];
        var bytesRead = gateway.Read(fileId, content.Length, buffer, 2);

        fileId.Should().Be(upload.FileId);
        bytesRead.Should().Be(4);
        buffer.Should().Equal(content[2..6]);
        cloud.HttpHandler.Calls.Should().HaveCount(2);
        cloud.HttpHandler.Calls[0].Method.Should().Be("POST");
        cloud.HttpHandler.Calls[0].Headers.Should().ContainKey("x-auth-token");
        cloud.HttpHandler.Calls[1].Range.Should().Be("bytes=0-7");
    }
}
