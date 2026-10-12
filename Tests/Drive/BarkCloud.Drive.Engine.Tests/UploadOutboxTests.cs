using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json.Nodes;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Drive.Engine;
using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using DokanNet;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

using Xunit;

using FileAccess = DokanNet.FileAccess;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class UploadOutboxTests
{
    [Fact]
    public void Server_key_normalizes_identity_host_and_includes_service_ports()
    {
        var key = EngineServerKey.FromConfig(new EngineConfig
        {
            Host = "EXAMPLE.COM",
            IdentityPort = 443,
            FilesPort = 7025,
            UsersPort = 7021,
        });

        key.Should().Be("https://example.com:443|files:7025|users:7021");
    }

    [Fact]
    public void Manifest_write_failure_keeps_file_data()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(dataPath, bytes);
        var outbox = new UploadOutbox(temp.Path("outbox"), (_, _) => throw new IOException("manifest write failed"));

        var act = () => outbox.Add(NewLocalFile(dataPath), "server", "42");

        act.Should().Throw<IOException>();
        File.ReadAllBytes(dataPath).Should().Equal(bytes);
        outbox.Count.Should().Be(0);
    }

    [Fact]
    public void Failed_move_keeps_temp_path_in_manifest()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        var bytes = new byte[] { 4, 5, 6 };
        File.WriteAllBytes(dataPath, bytes);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        using var openHandle = new FileStream(dataPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);

        var entry = outbox.Add(NewLocalFile(dataPath), "server", "42");

        entry.DataPath.Should().Be(dataPath);
        File.ReadAllBytes(dataPath).Should().Equal(bytes);
    }

    [Fact]
    public void Manifest_survives_recreation_and_registers_pending_file()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [9, 8, 7]);
        var outboxDirectory = temp.Path("outbox");
        var firstOutbox = new UploadOutbox(outboxDirectory);
        firstOutbox.Add(NewLocalFile(dataPath, "pending.bin"), "server", "42");
        var restoredOutbox = new UploadOutbox(outboxDirectory);
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), restoredOutbox, "server", () => "42");

        restoredOutbox.Count.Should().Be(1);
        fileSystem.FindFiles("\\", out var files, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        files.Should().ContainSingle(file => file.FileName == "pending.bin");
    }

    [Fact]
    public void Pending_file_blocks_writes_delete_and_move_and_reads_outbox_data()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [4, 5]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        outbox.Add(NewLocalFile(dataPath, "pending.bin"), "server", "42");
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");

        fileSystem.CreateFile("\\pending.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, new FakeDokanFileInfo()).Should().Be(DokanResult.AccessDenied);
        fileSystem.DeleteFile("\\pending.bin", new FakeDokanFileInfo()).Should().Be(DokanResult.AccessDenied);
        fileSystem.MoveFile("\\pending.bin", "\\renamed.bin", replace: false,
            new FakeDokanFileInfo()).Should().Be(DokanResult.AccessDenied);

        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\pending.bin", FileAccess.ReadData | FileAccess.GenericRead, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        var buffer = new byte[2];
        fileSystem.ReadFile("\\pending.bin", buffer, out var bytesRead, 0, reader).Should().Be(DokanResult.Success);
        bytesRead.Should().Be(2);
        buffer.Should().Equal([4, 5]);
        fileSystem.Cleanup("\\pending.bin", reader);
        fileSystem.CloseFile("\\pending.bin", reader);
    }

    [Fact]
    public void Delete_pending_on_pending_reader_keeps_outbox_data_and_manifest()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [4, 5]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(NewLocalFile(dataPath, "pending.bin"), "server", "42");
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\pending.bin", FileAccess.ReadData | FileAccess.Delete, FileShare.ReadWrite,
            FileMode.Open, FileOptions.DeleteOnClose, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        reader.DeletePending = true;

        fileSystem.Cleanup("\\pending.bin", reader);
        fileSystem.CloseFile("\\pending.bin", reader);

        File.Exists(entry.DataPath).Should().BeTrue();
        File.Exists(Path.Combine(outbox.DirectoryPath, entry.Id + ".json")).Should().BeTrue();
        cloud.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task Reader_opened_before_upload_can_finish_after_outbox_manifest_is_removed()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [4, 5]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(NewLocalFile(dataPath, "pending.bin"), "server", "42");
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        fileSystem.RegisterOutboxPending(entry);
        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\pending.bin", FileAccess.ReadData | FileAccess.GenericRead, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);

        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            _ => Task.CompletedTask, fileSystem.CompleteOutboxEntry, time: new FakeTimeProvider(),
            deferDataDeletion: fileSystem.DeferOutboxDataDeletion);
        worker.Start();
        await worker.TriggerAsync();

        outbox.Count.Should().Be(0);
        File.Exists(Path.Combine(outbox.DirectoryPath, entry.Id + ".json")).Should().BeFalse();
        File.Exists(entry.DataPath).Should().BeTrue();
        fileSystem.FindFiles("\\", out var pendingFiles, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        pendingFiles.Should().ContainSingle(file => file.FileName == "pending.bin");
        fileSystem.CreateFile("\\pending.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Truncate,
            FileOptions.None, FileAttributes.Normal, new FakeDokanFileInfo()).Should().Be(DokanResult.AccessDenied);
        var buffer = new byte[2];
        fileSystem.ReadFile("\\pending.bin", buffer, out var bytesRead, 0, reader).Should().Be(DokanResult.Success);
        bytesRead.Should().Be(2);
        buffer.Should().Equal([4, 5]);

        fileSystem.Cleanup("\\pending.bin", reader);
        fileSystem.CloseFile("\\pending.bin", reader);
        File.Exists(entry.DataPath).Should().BeFalse();
        fileSystem.FindFiles("\\", out var filesAfterClose, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        filesAfterClose.Should().BeEmpty();
    }

    [Fact]
    public async Task Pending_reader_close_returns_while_worker_upload_is_blocked()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [4, 5]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(NewLocalFile(dataPath, "pending.bin"), "server", "42");
        var cloud = new FakeCloud();
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = cloud.HttpHandler.Responder;
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                uploadStarted.TrySetResult();
                await releaseUpload.Task.WaitAsync(cancellationToken);
            }
            return await responder(request, cancellationToken);
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\pending.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        var localFile = ((HandleContext)reader.Context!).File!;
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            (entry, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(entry, cancellationToken),
            fileSystem.CompleteOutboxEntry,
            time: new FakeTimeProvider(), deferDataDeletion: fileSystem.DeferOutboxDataDeletion);
        worker.Start();
        var processing = Task.Run(worker.TriggerAsync);
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = Task.Run(() =>
        {
            fileSystem.Cleanup("\\pending.bin", reader);
            fileSystem.CloseFile("\\pending.bin", reader);
        });

        try
        {
            await closing.WaitAsync(TimeSpan.FromSeconds(1));
            localFile.Stream.Should().NotBeNull();
            localFile.Stream!.CanRead.Should().BeTrue();
        }
        finally
        {
            releaseUpload.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
        }

        outbox.Count.Should().Be(0);
        File.Exists(entry.DataPath).Should().BeFalse();
    }

    [Fact]
    public void Pending_reader_cleanup_and_close_do_not_upload_again()
    {
        using var temp = new TestDirectory();
        var sourcePath = temp.Path("source.bin");
        File.WriteAllBytes(sourcePath, [4, 5]);
        var local = NewLocalFile(sourcePath);
        local.Modified = true;
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(local, "server", "42");
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        fileSystem.RegisterOutboxPending(entry, local);

        var reader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\pending.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, reader).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\pending.bin", reader);
        fileSystem.CloseFile("\\pending.bin", reader);

        cloud.HttpHandler.Calls.Should().BeEmpty();
        gateway.ListDirectory("").Files.Should().BeEmpty();
        outbox.Count.Should().Be(1);
    }

    [Fact]
    public async Task Process_outbox_uses_current_entry_when_worker_snapshot_is_stale()
    {
        using var temp = new TestDirectory();
        var sourcePath = temp.Path("source.bin");
        File.WriteAllBytes(sourcePath, [7, 8]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var snapshot = outbox.Add(NewLocalFile(sourcePath, "race.bin"), "server", "42");
        var cloud = new FakeCloud();
        cloud.AddFile("", "race.bin", [7, 8]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var current = outbox.Find(snapshot.Id)!;
        current.Op = new PersistOperation { Stage = PersistStage.Done };
        current.Stage = PersistStage.Done;
        outbox.Save(current);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");

        await fileSystem.ProcessOutboxEntryAsync(snapshot);

        cloud.HttpHandler.Calls.Should().BeEmpty();
        gateway.ListDirectory("").Files.Should().ContainSingle(file => file.Entry.Name == "race.bin");
    }

    [Fact]
    public async Task Add_restarts_done_operation_for_modified_file_and_replaces_existing_entry()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var original = cloud.AddFile("", "updated.bin", [1, 2]);
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [8, 9, 10]);
        var localFile = NewLocalFile(dataPath, "updated.bin", new PersistOperation
        {
            Stage = PersistStage.Done,
            FileId = original.FileId,
            OldEntryDeleted = true,
            AttachAttempted = true,
            AttachCompleted = true,
        });
        localFile.ExistingEntryId = original.EntryId;
        localFile.Modified = true;
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(localFile, "server", "42");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            (entry, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(entry, cancellationToken),
            fileSystem.CompleteOutboxEntry,
            time: new FakeTimeProvider(), deferDataDeletion: fileSystem.DeferOutboxDataDeletion);
        worker.Start();

        await worker.TriggerAsync();

        entry.ExistingEntryId.Should().Be(original.EntryId);
        cloud.HttpHandler.Calls.Count(call => call.Method == "PUT").Should().Be(1);
        var saved = gateway.ListDirectory("").Files.Should().ContainSingle().Subject;
        saved.File.Id.Should().NotBe(original.FileId);
        await gateway.DownloadToAsync(saved.File.Id, temp.Path("saved.bin"), 3);
        File.ReadAllBytes(temp.Path("saved.bin")).Should().Equal([8, 9, 10]);
        outbox.Count.Should().Be(0);
    }

    [Fact]
    public void Updating_outbox_session_reuses_pending_file_with_open_handles()
    {
        using var temp = new TestDirectory();
        var sourcePath = temp.Path("source.bin");
        File.WriteAllBytes(sourcePath, [4, 5]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        outbox.Add(NewLocalFile(sourcePath, "pending.bin"), "server", "42");
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        var firstReader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\pending.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, firstReader).Should().Be(DokanResult.Success);
        var originalFile = ((HandleContext)firstReader.Context!).File!;

        fileSystem.UpdateOutboxSession(null);
        fileSystem.FindFiles("\\", out var loggedOut, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        loggedOut.Should().BeEmpty();
        fileSystem.UpdateOutboxSession("42");

        var secondReader = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\pending.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, secondReader).Should().Be(DokanResult.Success);
        ((HandleContext)secondReader.Context!).File.Should().BeSameAs(originalFile);
        fileSystem.Cleanup("\\pending.bin", firstReader);
        fileSystem.CloseFile("\\pending.bin", firstReader);
        fileSystem.Cleanup("\\pending.bin", secondReader);
        fileSystem.CloseFile("\\pending.bin", secondReader);
    }

    [Theory]
    [InlineData("other-server", "42")]
    [InlineData("server", "other-user")]
    public async Task Worker_skips_entries_for_another_server_or_owner(string entryServer, string entryOwner)
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [1]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        outbox.Add(NewLocalFile(dataPath), entryServer, entryOwner);
        var processed = 0;
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"), _ =>
        {
            Interlocked.Increment(ref processed);
            return Task.CompletedTask;
        }, time: new FakeTimeProvider());
        worker.Start();

        await worker.TriggerAsync();

        processed.Should().Be(0);
        outbox.Count.Should().Be(1);
    }

    [Fact]
    public async Task Worker_retries_after_thirty_seconds_using_fake_time()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [1]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        outbox.Add(NewLocalFile(dataPath), "server", "42");
        var time = new FakeTimeProvider();
        var secondPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = 0;
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"), _ =>
        {
            if (Interlocked.Increment(ref processed) == 1)
                throw new IOException("temporary failure");
            return Task.CompletedTask;
        }, completed: _ => secondPass.TrySetResult(), time: time);
        worker.Start();

        await worker.TriggerAsync();
        processed.Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(29));
        processed.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(1));
        await secondPass.Task.WaitAsync(TimeSpan.FromSeconds(5));
        outbox.Count.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_triggers_run_one_worker_pass()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [1]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        outbox.Add(NewLocalFile(dataPath), "server", "42");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = 0;
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"), async _ =>
        {
            Interlocked.Increment(ref processed);
            entered.TrySetResult();
            await release.Task;
        }, time: new FakeTimeProvider());
        worker.Start();

        var first = worker.TriggerAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.TriggerAsync();
        processed.Should().Be(1);

        release.SetResult();
        await first;
        outbox.Count.Should().Be(0);
    }

    [Fact]
    public async Task Trigger_during_pass_runs_again_for_a_new_outbox_entry()
    {
        using var temp = new TestDirectory();
        var outbox = new UploadOutbox(temp.Path("outbox"));
        File.WriteAllBytes(temp.Path("first.bin"), [1]);
        var firstEntry = outbox.Add(NewLocalFile(temp.Path("first.bin"), "first.bin"), "server", "42");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new System.Collections.Concurrent.ConcurrentBag<string>();
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"), async entry =>
        {
            processed.Add(entry.Id);
            if (entry.Id == firstEntry.Id)
            {
                entered.TrySetResult();
                await release.Task;
            }
        }, time: new FakeTimeProvider());
        worker.Start();

        var firstPass = worker.TriggerAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.WriteAllBytes(temp.Path("second.bin"), [2]);
        var secondEntry = outbox.Add(NewLocalFile(temp.Path("second.bin"), "second.bin"), "server", "42");
        await worker.TriggerAsync();

        release.SetResult();
        await firstPass;

        processed.Should().Contain(firstEntry.Id);
        processed.Should().Contain(secondEntry.Id);
        outbox.Count.Should().Be(0);
    }

    [Fact]
    public async Task CloseFile_stages_failed_persist_and_worker_does_not_run_during_cleanup()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.HttpHandler.Responder = (_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("TLS handshake failed", new AuthenticationException("certificate rejected")));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox,
            "server", () => "42");
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"), (entry, cancellationToken) =>
        {
            processStarted.TrySetResult();
            return fileSystem.ProcessOutboxEntryAsync(entry, cancellationToken);
        }, failed: (entry, exception) =>
        {
            fileSystem.SetOutboxSyncError(entry, exception);
            failureReported.TrySetResult();
        }, time: new FakeTimeProvider(), deferDataDeletion: fileSystem.DeferOutboxDataDeletion);
        worker.Start();
        fileSystem.OutboxAdded = () => _ = Task.Run(worker.TriggerAsync);

        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\retry.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var file = ((HandleContext)info.Context!).File!;
        fileSystem.WriteFile("\\retry.bin", [8, 9], out _, 0, info).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\retry.bin", info);

        fileSystem.LastSyncError.Should().Be(Loc.T("Eng_SyncFailedFmt", "retry.bin",
            Loc.T("Eng_TlsConnectionError")));
        outbox.Count.Should().Be(0);
        processStarted.Task.IsCompleted.Should().BeFalse();
        file.KeepData.Should().BeTrue();

        fileSystem.CloseFile("\\retry.bin", info);
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        outbox.Count.Should().Be(1);
        file.Kind.Should().Be(LocalFileKind.Pending);
        File.Exists(file.TempPath).Should().BeTrue();
        await failureReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fileSystem.LastSyncError.Should().Be(Loc.T("Eng_SyncFailedFmt", "retry.bin",
            Loc.T("Eng_TlsConnectionError")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Worker_resumes_legacy_delete_or_attach_stage_without_upload_session(bool deleteOldStage)
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var stage = deleteOldStage ? PersistStage.DeleteOld : PersistStage.Attach;
        var oldEntry = deleteOldStage
            ? cloud.AddFile("", "old-attach.bin", [1])
            : null;
        var uploadedFileId = cloud.AddUnattachedFile([7, 8, 9]);
        cloud.Invoker.FailNext(FakeCloudMethods.AttachFile,
            new RpcException(new Status(StatusCode.Unavailable, "Attach unavailable")));
        cloud.Invoker.FailNext(FakeCloudMethods.AttachFile,
            new RpcException(new Status(StatusCode.Unavailable, "Attach unavailable")));
        var dataPath = temp.Path("source.bin");
        File.WriteAllBytes(dataPath, [7, 8, 9]);
        var operation = new PersistOperation
        {
            Stage = stage,
            FileId = uploadedFileId,
            OldEntryDeleted = stage == PersistStage.Attach,
            AttachAttempted = true,
        };
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var local = NewLocalFile(dataPath, "attach.bin", operation);
        if (oldEntry is not null)
            local.ExistingEntryId = oldEntry.EntryId;
        var entry = outbox.Add(local, "server", "42");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox,
            "server", () => "42");
        fileSystem.SetLastSyncError("предыдущая ошибка");
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            (entry, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(entry, cancellationToken),
            fileSystem.CompleteOutboxEntry,
            (_, ex) => fileSystem.SetLastSyncError(ex.Message), new FakeTimeProvider(),
            fileSystem.DeferOutboxDataDeletion);
        worker.Start();

        await worker.TriggerAsync();
        var saved = outbox.Find(entry.Id)!;
        saved.Stage.Should().Be(PersistStage.Attach);
        saved.FileId.Should().Be(uploadedFileId);
        saved.Attempts.Should().Be(1);
        if (oldEntry is not null)
            cloud.IsInTrash(oldEntry.EntryId).Should().BeTrue();
        cloud.HttpHandler.Calls.Should().BeEmpty();

        await worker.TriggerAsync();

        cloud.HttpHandler.Calls.Should().BeEmpty();
        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession);
        cloud.Invoker.Calls.Where(call => call.MethodFullName == FakeCloudMethods.AttachFile)
            .Select(call => (AttachFileRequest)call.Request)
            .Should().OnlyContain(request => string.IsNullOrEmpty(request.UploadSessionId));
        outbox.Count.Should().Be(0);
        File.Exists(entry.DataPath).Should().BeFalse();
        File.Exists(Path.Combine(outbox.DirectoryPath, entry.Id + ".json")).Should().BeFalse();
        fileSystem.LastSyncError.Should().BeNull();
    }

    [Fact]
    public async Task Worker_uploads_legacy_upload_stage_manifest_with_upload2()
    {
        using var temp = new TestDirectory();
        var dataPath = temp.Path("legacy.bin");
        File.WriteAllBytes(dataPath, [7, 8, 9]);
        var outboxPath = temp.Path("outbox");
        var originalOutbox = new UploadOutbox(outboxPath);
        var original = originalOutbox.Add(NewLocalFile(dataPath, "legacy.bin", new PersistOperation
        {
            Stage = PersistStage.Upload,
        }), "server", "42");
        var manifestPath = Path.Combine(outboxPath, original.Id + ".json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["Op"]!.AsObject().Remove("Upload2");
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        var outbox = new UploadOutbox(outboxPath);
        var legacy = outbox.Find(original.Id)!;
        legacy.Stage.Should().Be(PersistStage.Upload);
        legacy.Op!.Upload2.Should().BeNull();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            (entry, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(entry, cancellationToken),
            fileSystem.CompleteOutboxEntry,
            failed: (_, ex) => throw ex,
            time: new FakeTimeProvider(),
            deferDataDeletion: fileSystem.DeferOutboxDataDeletion);
        worker.Start();

        await worker.TriggerAsync();

        var created = cloud.Invoker.Calls.Single(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession);
        ((CreateUploadSessionRequest)created.Request).FileName.Should().Be("legacy.bin");
        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.GetUploadUrl);
        cloud.HttpHandler.Calls.Should().OnlyContain(call => call.Method == "PUT");
        var attach = (AttachFileRequest)cloud.Invoker.Calls
            .Single(call => call.MethodFullName == FakeCloudMethods.AttachFile).Request;
        attach.Name.Should().Be("legacy.bin");
        attach.UploadSessionId.Should().NotBeNullOrWhiteSpace();
        outbox.Count.Should().Be(0);
    }

    [Fact]
    public async Task Background_trigger_returns_while_upload_is_blocked()
    {
        using var temp = new TestDirectory();
        File.WriteAllBytes(temp.Path("source.bin"), [1]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        outbox.Add(NewLocalFile(temp.Path("source.bin")), "server", "42");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"), async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        }, time: new FakeTimeProvider());
        worker.Start();
        var trigger = Task.Run(worker.Trigger);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await trigger.WaitAsync(TimeSpan.FromSeconds(1));
            outbox.Count.Should().Be(1);
        }
        finally
        {
            release.TrySetResult();
            await trigger;
            await worker.StopAsync();
        }
        outbox.Count.Should().Be(0);
    }

    [Fact]
    public async Task Stop_cancels_outbox_upload_and_preserves_saved_session_and_data()
    {
        using var temp = new TestDirectory();
        var sourcePath = temp.Path("source.bin");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(NewLocalFile(sourcePath, "stopped.bin"), "server", "42");
        var manifestPath = Path.Combine(outbox.DirectoryPath, entry.Id + ".json");
        var cloud = new FakeCloud();
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = cloud.HttpHandler.Responder;
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                uploadStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await responder(request, cancellationToken);
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        var failedCalls = 0;
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            (snapshot, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(snapshot, cancellationToken),
            failed: (_, _) => Interlocked.Increment(ref failedCalls),
            time: new FakeTimeProvider());
        worker.Start();
        var pass = worker.TriggerAsync();
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await pass.WaitAsync(TimeSpan.FromSeconds(3));

        var saved = outbox.Find(entry.Id)!;
        saved.Stage.Should().Be(PersistStage.Upload);
        saved.Attempts.Should().Be(0);
        failedCalls.Should().Be(0);
        saved.Op!.Upload2.Should().NotBeNull();
        saved.Op.Upload2!.SessionId.Should().NotBeNullOrWhiteSpace();
        new UploadOutbox(outbox.DirectoryPath).Find(entry.Id)!.Op!.Upload2!.SessionId
            .Should().Be(saved.Op.Upload2.SessionId);
        File.Exists(manifestPath).Should().BeTrue();
        File.ReadAllBytes(entry.DataPath).Should().Equal([1, 2, 3]);
    }

    [Theory]
    [MemberData(nameof(StopCancellationErrors))]
    public async Task Start_after_stop_retries_cancelled_pass_without_counting_an_attempt(Exception stopError)
    {
        using var temp = new TestDirectory();
        File.WriteAllBytes(temp.Path("source.bin"), [1]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(NewLocalFile(temp.Path("source.bin")), "server", "42");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var failedCalls = 0;
        var completedCalls = 0;
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            async (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                    throw stopError;
                }

                secondStarted.TrySetResult();
                await releaseSecond.Task;
            },
            _ => Interlocked.Increment(ref completedCalls),
            (_, _) => Interlocked.Increment(ref failedCalls),
            new FakeTimeProvider());
        worker.Start();
        var firstPass = worker.TriggerAsync();
        Task? stopping = null;
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = worker.StopAsync();
            worker.Start();
            releaseFirst.TrySetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

            outbox.Find(entry.Id)!.Attempts.Should().Be(0);
            failedCalls.Should().Be(0);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            if (stopping is not null)
                await stopping.WaitAsync(TimeSpan.FromSeconds(15));
            await firstPass.WaitAsync(TimeSpan.FromSeconds(15));
        }

        await WaitUntilAsync(() => outbox.Count == 0 && Volatile.Read(ref completedCalls) == 1);
        calls.Should().Be(2);
        failedCalls.Should().Be(0);
    }

    public static IEnumerable<object[]> StopCancellationErrors()
    {
        yield return [new RpcException(new Status(StatusCode.Cancelled, "cancelled"))];
        yield return [new AggregateException("cancelled", new IOException("transport stopped"),
            new RpcException(new Status(StatusCode.Cancelled, "cancelled")))];
    }

    [Fact]
    public async Task Timeout_failure_is_counted_as_a_regular_outbox_attempt()
    {
        using var temp = new TestDirectory();
        File.WriteAllBytes(temp.Path("source.bin"), [1]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(NewLocalFile(temp.Path("source.bin")), "server", "42");
        var error = new TimeoutException(Loc.T("Eng_HttpTransferTimedOut"));
        Exception? callbackError = null;
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            (_, _) => Task.FromException(error),
            failed: (_, failure) => callbackError = failure,
            time: new FakeTimeProvider());
        worker.Start();

        await worker.TriggerAsync();

        var saved = outbox.Find(entry.Id)!;
        saved.Attempts.Should().Be(1);
        saved.LastError.Should().Be(error.Message);
        callbackError.Should().BeSameAs(error);
    }

    [Fact]
    public void Offline_staging_uses_the_remembered_owner_for_the_same_server()
    {
        using var temp = new TestDirectory();
        var settings = new EngineSettingsStore(temp.Path("settings"));
        settings.SetLastUserId("server", "42");
        settings.SetLastUserId("other-server", "99");
        var restoredSettings = new EngineSettingsStore(temp.Path("settings"));
        restoredSettings.GetLastUserId("server").Should().Be("42");
        restoredSettings.GetLastUserId("unknown-server").Should().BeNull();
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var cloud = new FakeCloud();
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server",
            () => restoredSettings.GetLastUserId("server"));
        var info = new FakeDokanFileInfo();
        fs.CreateFile("\\offline.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        fs.WriteFile("\\offline.bin", [7], out _, 0, info).Should().Be(DokanResult.Success);
        fs.Cleanup("\\offline.bin", info);
        fs.CloseFile("\\offline.bin", info);
        outbox.Entries.Should().ContainSingle().Which.OwnerUserId.Should().Be("42");
    }

    [Fact]
    public async Task Authenticated_session_adopts_only_unowned_entries_for_its_server()
    {
        using var temp = new TestDirectory();
        var outbox = new UploadOutbox(temp.Path("outbox"));
        File.WriteAllBytes(temp.Path("own.bin"), [1]);
        File.WriteAllBytes(temp.Path("foreign.bin"), [2]);
        var own = outbox.Add(NewLocalFile(temp.Path("own.bin"), "own.bin"), "server", null);
        var foreign = outbox.Add(NewLocalFile(temp.Path("foreign.bin"), "foreign.bin"), "foreign-server", null);
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        fs.FindFiles("\\", out var before, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        before.Should().BeEmpty();
        fs.AdoptOutboxForSession("42");
        var reloaded = new UploadOutbox(outbox.DirectoryPath);
        reloaded.Find(own.Id)!.OwnerUserId.Should().Be("42");
        reloaded.Find(foreign.Id)!.OwnerUserId.Should().BeNull();
        fs.FindFiles("\\", out var after, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        after.Should().ContainSingle(file => file.FileName == "own.bin");
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            _ => throw new IOException("offline"), time: new FakeTimeProvider());
        worker.Start();
        await worker.TriggerAsync();
        outbox.Find(own.Id)!.Attempts.Should().Be(1);
        outbox.Find(foreign.Id)!.Attempts.Should().Be(0);
    }

    [Fact]
    public void Pending_visibility_changes_with_owner_and_logout_without_removing_data()
    {
        using var temp = new TestDirectory();
        File.WriteAllBytes(temp.Path("source.bin"), [1]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(NewLocalFile(temp.Path("source.bin"), "private.bin"), "server", "A");
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "B");
        fs.FindFiles("\\", out var foreign, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        foreign.Should().BeEmpty();
        fs.CreateFile("\\private.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, new FakeDokanFileInfo()).Should().Be(DokanResult.FileNotFound);
        fs.UpdateOutboxSession("A");
        fs.FindFiles("\\", out var own, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        own.Should().ContainSingle(file => file.FileName == "private.bin");
        fs.UpdateOutboxSession(null);
        fs.FindFiles("\\", out var loggedOut, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        loggedOut.Should().BeEmpty();
        outbox.Count.Should().Be(1);
        File.Exists(entry.DataPath).Should().BeTrue();
        var writer = new FakeDokanFileInfo();
        fs.CreateFile("\\private.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, writer).Should().Be(DokanResult.Success);
        writer.DeletePending = true;
        fs.Cleanup("\\private.bin", writer);
        fs.CloseFile("\\private.bin", writer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_pending_discards_local_data_even_after_failed_persist_or_placeholder(bool placeholder)
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        var info = new FakeDokanFileInfo();
        fs.CreateFile("\\delete.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var local = ((HandleContext)info.Context!).File!;
        if (!placeholder)
            fs.WriteFile("\\delete.bin", [7], out _, 0, info).Should().Be(DokanResult.Success);
        fs.Cleanup("\\delete.bin", info);
        if (placeholder)
        {
            fs.CloseFile("\\delete.bin", info);
            fs.CreateFile("\\delete.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
                FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        }
        info.DeletePending = true;
        fs.Cleanup("\\delete.bin", info);
        fs.CloseFile("\\delete.bin", info);
        outbox.Count.Should().Be(0);
        File.Exists(local.TempPath).Should().BeFalse();
        fs.FindFiles("\\", out var files, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        files.Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_delays_cover_all_steps_and_stay_at_five_minutes()
    {
        using var temp = new TestDirectory();
        File.WriteAllBytes(temp.Path("source.bin"), [1]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        outbox.Add(NewLocalFile(temp.Path("source.bin")), "server", "42");
        var time = new ObservedTimeProvider();
        var calls = 0;
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"), _ =>
        {
            Interlocked.Increment(ref calls);
            throw new IOException("offline");
        }, time: time);
        worker.Start();
        await worker.TriggerAsync();
        var expected = 1;
        foreach (var delay in new[] { 30, 60, 120, 300, 300, 300 })
        {
            time.Advance(TimeSpan.FromSeconds(delay - 1));
            Volatile.Read(ref calls).Should().Be(expected);
            time.Advance(TimeSpan.FromSeconds(1));
            expected++;
            await WaitUntilAsync(() => Volatile.Read(ref calls) == expected
                && outbox.Entries.Single().Attempts == expected && time.Changes == expected + 1);
        }
    }

    private sealed class ObservedTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _time = new();
        private int _changes;
        public int Changes => Volatile.Read(ref _changes);
        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();
        public void Advance(TimeSpan delay) => _time.Advance(delay);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new ObservedTimer(_time.CreateTimer(callback, state, dueTime, period), this);

        private sealed class ObservedTimer(ITimer timer, ObservedTimeProvider time) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var changed = timer.Change(dueTime, period);
                Interlocked.Increment(ref time._changes);
                return changed;
            }
            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Missing_confirmed_attachment_is_recreated_with_the_same_blob()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var original = cloud.AddFile("", "reattach.bin", [7, 8]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        gateway.DeleteFileEntry(original.EntryId);
        File.WriteAllBytes(temp.Path("source.bin"), [7, 8]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        outbox.Add(NewLocalFile(temp.Path("source.bin"), "reattach.bin", new PersistOperation
        {
            Stage = PersistStage.Attach,
            FileId = original.FileId,
            AttachAttempted = true,
            AttachCompleted = true,
        }), "server", "42");
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            (entry, cancellationToken) => fs.ProcessOutboxEntryAsync(entry, cancellationToken),
            fs.CompleteOutboxEntry, time: new FakeTimeProvider(),
            deferDataDeletion: fs.DeferOutboxDataDeletion);
        worker.Start();
        await worker.TriggerAsync();
        outbox.Count.Should().Be(0);
        gateway.ListDirectory("").Files.Should().ContainSingle(file => file.File.Id == original.FileId);
        cloud.HttpHandler.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Remove_keeps_locked_data_after_deleting_manifest()
    {
        using var temp = new TestDirectory();
        File.WriteAllBytes(temp.Path("source.bin"), [7]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(NewLocalFile(temp.Path("source.bin")), "server", "42");
        using var reader = new FileStream(entry.DataPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        var remove = () => outbox.Remove(entry);
        remove.Should().NotThrow();
        File.Exists(Path.Combine(outbox.DirectoryPath, entry.Id + ".json")).Should().BeFalse();
        File.Exists(entry.DataPath).Should().BeTrue();
        new UploadOutbox(outbox.DirectoryPath).Count.Should().Be(0);
    }

    [Fact]
    public async Task Worker_does_not_reprocess_successful_upload_when_outbox_data_is_locked()
    {
        using var temp = new TestDirectory();
        var sourcePath = temp.Path("source.bin");
        File.WriteAllBytes(sourcePath, [7]);
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var entry = outbox.Add(NewLocalFile(sourcePath, "locked.bin"), "server", "42");
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server", () => "42");
        var completed = 0;
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "42"),
            (snapshot, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(snapshot, cancellationToken),
            _ => Interlocked.Increment(ref completed),
            time: new FakeTimeProvider(), deferDataDeletion: fileSystem.DeferOutboxDataDeletion);
        using var reader = new FileStream(entry.DataPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        worker.Start();

        await worker.TriggerAsync();
        await worker.TriggerAsync();

        completed.Should().Be(1);
        outbox.Count.Should().Be(0);
        cloud.HttpHandler.Calls.Count(call => call.Method == "PUT").Should().Be(1);
        gateway.ListDirectory("").Files.Should().ContainSingle(file => file.Entry.Name == "locked.bin");
    }

    [Fact]
    public async Task Save_and_remove_serialize_manifest_file_operations()
    {
        using var temp = new TestDirectory();
        var outboxDirectory = temp.Path("outbox");
        var enteredWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockNextWrite = 0;
        var outbox = new UploadOutbox(outboxDirectory, (path, json) =>
        {
            File.WriteAllText(path, json);
            if (Interlocked.Exchange(ref blockNextWrite, 0) == 1)
            {
                enteredWrite.TrySetResult();
                releaseWrite.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        });
        var id = Guid.NewGuid().ToString("D");
        var dataPath = Path.Combine(outboxDirectory, id + ".bin");
        File.WriteAllBytes(dataPath, [1]);
        var entry = new UploadOutboxEntry
        {
            Id = id,
            DataPath = dataPath,
            Name = "saved.bin",
            ServerKey = "server",
            OwnerUserId = "42",
        };
        outbox.Save(entry);

        var updated = entry.Copy();
        updated.Attempts = 1;
        blockNextWrite = 1;
        var saving = Task.Run(() => outbox.Save(updated));
        await enteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var removalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removing = Task.Run(() =>
        {
            removalStarted.TrySetResult();
            outbox.Remove(updated);
        });

        try
        {
            await removalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            releaseWrite.TrySetResult();
            await Task.WhenAll(saving, removing);
        }
        finally
        {
            releaseWrite.TrySetResult();
        }

        outbox.Count.Should().Be(0);
        File.Exists(Path.Combine(outboxDirectory, id + ".json")).Should().BeFalse();
        File.Exists(Path.Combine(outboxDirectory, id + ".json.tmp")).Should().BeFalse();
        File.Exists(dataPath).Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(1, timeout.Token);
    }

    private static LocalFile NewLocalFile(string dataPath, string name = "pending.bin",
        PersistOperation? operation = null) => new()
    {
        Path = "\\" + name,
        Name = name,
        DirectoryId = "",
        TempPath = dataPath,
        Kind = LocalFileKind.Writable,
        Op = operation,
    };
}
