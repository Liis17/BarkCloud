using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using DokanNet;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

using Xunit;

using FileAccess = DokanNet.FileAccess;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class ResumableUploaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void Persist_uses_upload2_for_every_file_size_and_attaches_by_session_id(int size)
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.UploadPartSize = 3;
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var data = Enumerable.Range(0, size).Select(index => (byte)index).ToArray();
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\upload.bin", data);

        fileSystem.Cleanup("\\upload.bin", info);
        fileSystem.CloseFile("\\upload.bin", info);

        var parts = cloud.HttpHandler.Calls.Where(call => call.Method == "PUT").ToArray();
        var partCount = (size + 2) / 3;
        parts.Should().HaveCount(partCount);
        parts.Select(call => call.ContentRange).Should().Equal(Enumerable.Range(0, partCount)
            .Select(index => $"bytes {index * 3}-{Math.Min(index * 3 + 2, size - 1)}/{size}"));
        parts.Select(call => call.Headers["Content-Length"]).Should().Equal(Enumerable.Range(0, partCount)
            .Select(index => $"{Math.Min(3, size - index * 3)}"));
        parts.Should().OnlyContain(call => call.Url.StartsWith("https://files.test/file-upload/", StringComparison.Ordinal)
                                           && call.Headers["Content-Type"] == "application/octet-stream"
                                           && !string.IsNullOrWhiteSpace(call.Headers["X-Upload-Token"]));
        cloud.HttpHandler.Calls.Should().OnlyContain(call => call.Method == "PUT");
        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.GetUploadUrl);

        var create = cloud.Invoker.Calls.Single(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession);
        var description = (CreateUploadSessionRequest)create.Request;
        description.FileName.Should().Be("upload.bin");
        description.FileSize.Should().Be(size);
        description.ContentType.Should().Be("application/octet-stream");
        description.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(data)));

        var attach = (AttachFileRequest)cloud.Invoker.Calls
            .Single(call => call.MethodFullName == FakeCloudMethods.AttachFile).Request;
        attach.Name.Should().Be("upload.bin");
        attach.UploadSessionId.Should().Be(local.Op!.Upload2!.SessionId);
        attach.IsUploadRetry.Should().BeFalse();
    }

    [Fact]
    public async Task Retry_sends_only_missing_parts_and_parts_without_etag()
    {
        using var temp = new TestDirectory();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [0, 1, 2, 3, 4, 5, 6, 7]);
        var cloud = new FakeCloud { FailUploadPartNumberOnce = 2 };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var operation = new PersistOperation();

        await FluentActions.Invoking(() => gateway.UploadResumableAsync(operation, path, "parts.bin",
                () => { }, TimeSpan.FromMinutes(2)))
            .Should().ThrowAsync<HttpRequestException>();
        (await gateway.UploadResumableAsync(operation, path, "parts.bin", () => { }, TimeSpan.FromMinutes(2)))
            .Should().Be(operation.FileId);

        cloud.HttpHandler.Calls.Where(call => call.Method == "PUT")
            .Select(call => call.Url.Split('/').Last())
            .Should().Equal("1", "2", "2", "3");
    }

    [Fact]
    public async Task Lost_create_response_retries_with_same_key_and_returns_same_session()
    {
        using var temp = new TestDirectory();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var cloud = new FakeCloud();
        cloud.Invoker.LoseNextResponseAfterEffect(FakeCloudMethods.CreateUploadSession);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var operation = new PersistOperation();

        await gateway.UploadResumableAsync(operation, path, "lost-create.bin", () => { }, TimeSpan.FromMinutes(2));

        var creates = cloud.Invoker.Calls.Where(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession)
            .Select(call => (CreateUploadSessionRequest)call.Request).ToArray();
        creates.Should().HaveCount(2);
        creates.Select(request => request.IdempotencyKey).Distinct().Should().ContainSingle();
        cloud.UploadSessionIds.Should().ContainSingle().Which.Should().Be(operation.Upload2!.SessionId);
    }

    [Theory]
    [InlineData(UploadSessionStatus.Failed)]
    [InlineData(UploadSessionStatus.Cancelled)]
    [InlineData(UploadSessionStatus.Expired)]
    public async Task Terminal_session_retries_with_a_new_idempotency_key(UploadSessionStatus status)
    {
        using var temp = new TestDirectory();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var cloud = new FakeCloud();
        cloud.UploadCompleteStatuses.Enqueue(status);
        cloud.UploadCompleteStatuses.Enqueue(UploadSessionStatus.Ready);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var operation = new PersistOperation();

        await gateway.UploadResumableAsync(operation, path, "terminal.bin", () => { }, TimeSpan.FromMinutes(2));

        var creates = cloud.Invoker.Calls.Where(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession)
            .Select(call => (CreateUploadSessionRequest)call.Request).ToArray();
        creates.Should().HaveCount(2);
        creates[0].IdempotencyKey.Should().NotBe(creates[1].IdempotencyKey);
        operation.Upload2!.IdempotencyKey.Should().Be(creates[1].IdempotencyKey);
    }

    [Fact]
    public void Retried_attach_sets_upload_session_id_and_retry_flag()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.Invoker.FailNext(FakeCloudMethods.AttachFile,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\retry-attach.bin", [1, 2, 3, 4]);

        fileSystem.Cleanup("\\retry-attach.bin", info);
        fileSystem.CloseFile("\\retry-attach.bin", info);

        var attaches = cloud.Invoker.Calls.Where(call => call.MethodFullName == FakeCloudMethods.AttachFile)
            .Select(call => (AttachFileRequest)call.Request).ToArray();
        attaches.Should().HaveCount(2);
        attaches.Select(request => request.UploadSessionId).Should()
            .OnlyContain(sessionId => sessionId == local.Op!.Upload2!.SessionId);
        attaches.Select(request => request.IsUploadRetry).Should().Equal(false, true);
    }

    [Fact]
    public async Task Idempotency_conflict_retries_with_a_new_key()
    {
        using var temp = new TestDirectory();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var cloud = new FakeCloud();
        cloud.Invoker.FailNext(FakeCloudMethods.CreateUploadSession,
            FakeCallInvoker.BusinessError(ServerErrors.UploadIdempotencyConflict));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var operation = new PersistOperation();

        await gateway.UploadResumableAsync(operation, path, "conflict.bin", () => { }, TimeSpan.FromMinutes(2));

        var creates = cloud.Invoker.Calls.Where(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession)
            .Select(call => (CreateUploadSessionRequest)call.Request).ToArray();
        creates.Should().HaveCount(2);
        creates[0].IdempotencyKey.Should().NotBe(creates[1].IdempotencyKey);
        operation.Upload2!.IdempotencyKey.Should().Be(creates[1].IdempotencyKey);
    }

    [Fact]
    public async Task Processing_past_two_minutes_moves_to_outbox_and_worker_finishes_attach()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        using var temp = new TestDirectory();
        var time = new FakeTimeProvider();
        var cloud = new FakeCloud();
        cloud.UploadCompleteStatuses.Enqueue(UploadSessionStatus.Processing);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var outbox = new UploadOutbox(temp.Path("outbox"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox,
            "server", () => "owner", time);
        var firstWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResumableProcessingWaitStartedForTesting = _ => firstWaitStarted.TrySetResult();
        var (info, _) = OpenWritableAndWrite(fileSystem, "\\processing.bin", [1, 2, 3, 4]);
        var closeTask = Task.Run(() =>
        {
            fileSystem.Cleanup("\\processing.bin", info);
            fileSystem.CloseFile("\\processing.bin", info);
        });

        await firstWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromMinutes(2));
        await closeTask.WaitAsync(TimeSpan.FromSeconds(10));

        var pending = outbox.Entries.Should().ContainSingle().Subject;
        pending.Stage.Should().Be(PersistStage.Processing);
        pending.Op!.Upload2.Should().NotBeNull();
        pending.Op!.Upload2!.SessionId.Should().NotBeNullOrEmpty();
        pending.Attempts = 2;
        pending.LastError = "previous error";
        outbox.Save(pending);
        var sessionId = pending.Op!.Upload2.SessionId!;
        var manifest = File.ReadAllText(Path.Combine(outbox.DirectoryPath, pending.Id + ".json"));
        manifest.Should().Contain("\"Upload2\"");
        manifest.Should().Contain("\"SessionId\"");

        var secondPath = temp.Path("second.bin");
        File.WriteAllBytes(secondPath, [9]);
        outbox.Save(new UploadOutboxEntry { Id = "second", DataPath = secondPath, Name = "second.bin",
            ServerKey = "server", OwnerUserId = "owner", Op = new PersistOperation() });
        var workerWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResumableProcessingWaitStartedForTesting = _ => workerWaitStarted.TrySetResult();
        await using var worker = new UploadOutboxWorker(outbox,
            () => ("server", "owner"),
            (entry, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(entry, cancellationToken),
            completed: fileSystem.CompleteOutboxEntry,
            failed: fileSystem.SetOutboxSyncError,
            time: new FakeTimeProvider(),
            deferDataDeletion: fileSystem.DeferOutboxDataDeletion);
        worker.Start();
        var pass = worker.TriggerAsync();
        await workerWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromMinutes(2));
        await pass.WaitAsync(TimeSpan.FromSeconds(10));
        outbox.Count.Should().Be(1);
        outbox.Find(pending.Id)!.Attempts.Should().Be(2);
        outbox.Find(pending.Id)!.LastError.Should().Be("previous error");
        fileSystem.LastSyncError.Should().BeNullOrEmpty();
        cloud.Invoker.Calls.Where(call => call.MethodFullName == FakeCloudMethods.AttachFile)
            .Select(call => ((AttachFileRequest)call.Request).Name).Should().Equal("second.bin");
        cloud.SetUploadSessionStatus(sessionId, UploadSessionStatus.Ready);
        await worker.TriggerAsync().WaitAsync(TimeSpan.FromSeconds(10));

        outbox.Count.Should().Be(0);
        ((AttachFileRequest)cloud.Invoker.Calls.Single(call => call.MethodFullName == FakeCloudMethods.AttachFile
                && ((AttachFileRequest)call.Request).Name == "processing.bin").Request)
            .UploadSessionId.Should().Be(sessionId);
    }

    [Fact]
    public async Task Cleanup_does_not_repeat_persist_when_upload_is_processing()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        using var temp = new TestDirectory();
        var time = new FakeTimeProvider();
        var cloud = new FakeCloud();
        cloud.UploadCompleteStatuses.Enqueue(UploadSessionStatus.Processing);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var outbox = new UploadOutbox(temp.Path("outbox"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox,
            "server", () => "owner", time);
        var firstWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResumableProcessingWaitStartedForTesting = _ => firstWaitStarted.TrySetResult();
        var (firstInfo, local) = OpenWritableAndWrite(fileSystem, "\\processing-cleanup.bin", [1, 2, 3, 4]);
        var secondInfo = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\processing-cleanup.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, secondInfo).Should().Be(DokanResult.Success);

        fileSystem.Cleanup("\\processing-cleanup.bin", firstInfo);
        var secondCleanup = Task.Run(() => fileSystem.Cleanup("\\processing-cleanup.bin", secondInfo));
        await firstWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromMinutes(2));
        await secondCleanup.WaitAsync(TimeSpan.FromSeconds(10));
        local.Op!.Stage.Should().Be(PersistStage.Processing);
        local.KeepData.Should().BeTrue();

        var repeatedWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repeatedWaits = 0;
        gateway.ResumableProcessingWaitStartedForTesting = _ =>
        {
            Interlocked.Increment(ref repeatedWaits);
            repeatedWaitStarted.TrySetResult();
        };
        var thirdInfo = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\processing-cleanup.bin", FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, thirdInfo).Should().Be(DokanResult.Success);
        var thirdCleanup = Task.Run(() => fileSystem.Cleanup("\\processing-cleanup.bin", thirdInfo));
        if (await Task.WhenAny(thirdCleanup, repeatedWaitStarted.Task) == repeatedWaitStarted.Task)
            time.Advance(TimeSpan.FromMinutes(2));
        await thirdCleanup.WaitAsync(TimeSpan.FromSeconds(10));
        repeatedWaits.Should().Be(0);

        var data = new byte[4];
        fileSystem.ReadFile("\\processing-cleanup.bin", data, out var bytesRead, 0, thirdInfo)
            .Should().Be(DokanResult.Success);
        bytesRead.Should().Be(4);
        data.Should().Equal([1, 2, 3, 4]);

        fileSystem.CloseFile("\\processing-cleanup.bin", firstInfo);
        fileSystem.CloseFile("\\processing-cleanup.bin", secondInfo);
        fileSystem.CloseFile("\\processing-cleanup.bin", thirdInfo);
        var pending = outbox.Entries.Should().ContainSingle().Subject;
        pending.Stage.Should().Be(PersistStage.Processing);
        File.ReadAllBytes(pending.DataPath).Should().Equal([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Stop_during_processing_wait_does_not_count_or_report_an_attempt()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        using var temp = new TestDirectory();
        var time = new FakeTimeProvider();
        var cloud = new FakeCloud();
        var session = new FilesApi.FilesApiClient(cloud.Invoker).CreateUploadSession(new CreateUploadSessionRequest
        {
            IdempotencyKey = "processing-stop",
            FileName = "processing-stop.bin",
            FileSize = 1,
        });
        cloud.SetUploadSessionStatus(session.SessionId, UploadSessionStatus.Processing);
        var path = temp.Path("processing-stop.bin");
        await File.WriteAllBytesAsync(path, [1]);
        var outbox = new UploadOutbox(temp.Path("outbox"), time);
        var entry = new UploadOutboxEntry
        {
            Id = "processing-stop",
            DataPath = path,
            Name = "processing-stop.bin",
            ServerKey = "server",
            OwnerUserId = "owner",
            Stage = PersistStage.Processing,
            Attempts = 2,
            LastError = "previous error",
            Op = new PersistOperation
            {
                Stage = PersistStage.Processing,
                Upload2 = new Upload2State { IdempotencyKey = "processing-stop", Name = "processing-stop.bin",
                    Size = 1, SessionId = session.SessionId },
            },
        };
        outbox.Save(entry);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox,
            "server", () => "owner", time);
        var processingWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResumableProcessingWaitStartedForTesting = _ => processingWaitStarted.TrySetResult();
        var failedCalls = 0;
        await using var worker = new UploadOutboxWorker(outbox, () => ("server", "owner"),
            (snapshot, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(snapshot, cancellationToken),
            failed: (_, _) => Interlocked.Increment(ref failedCalls), time: time);
        worker.Start();

        var pass = worker.TriggerAsync();
        await processingWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await pass.WaitAsync(TimeSpan.FromSeconds(10));

        var saved = outbox.Find(entry.Id)!;
        saved.Stage.Should().Be(PersistStage.Processing);
        saved.Attempts.Should().Be(2);
        saved.LastError.Should().Be("previous error");
        failedCalls.Should().Be(0);
    }

    [Fact]
    public void CloseFile_does_not_repeat_persist_for_processing_stage()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox,
            "server", () => "owner");
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\processing-close.bin", [1]);
        var session = new FilesApi.FilesApiClient(cloud.Invoker).CreateUploadSession(new CreateUploadSessionRequest
        { IdempotencyKey = "processing-close", FileName = "processing-close.bin", FileSize = 1 });
        cloud.SetUploadSessionStatus(session.SessionId, UploadSessionStatus.Processing);
        local.Op = new PersistOperation
        {
            Stage = PersistStage.Processing,
            Upload2 = new Upload2State { IdempotencyKey = "processing-close", Name = "processing-close.bin",
                Size = 1, SessionId = session.SessionId },
        };
        local.KeepData = true;

        fileSystem.CloseFile("\\processing-close.bin", info);

        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.GetUploadSession).Should().Be(0);
        outbox.Entries.Should().ContainSingle().Which.Stage.Should().Be(PersistStage.Processing);
    }

    [Fact]
    public void Outbox_reads_legacy_manifest_without_upload2()
    {
        using var temp = new TestDirectory();
        var outboxDir = temp.Path("outbox");
        Directory.CreateDirectory(outboxDir);
        var id = Guid.NewGuid().ToString("D");
        var dataPath = Path.Combine(outboxDir, id + ".bin");
        File.WriteAllBytes(dataPath, [1]);
        File.WriteAllText(Path.Combine(outboxDir, id + ".json"), JsonSerializer.Serialize(new
        {
            Id = id,
            DataPath = dataPath,
            Name = "legacy.bin",
            DirectoryId = "",
            ServerKey = "server",
            OwnerUserId = "owner",
            Stage = PersistStage.DeleteOld,
            FileId = "old-file-id",
            Op = new { Stage = PersistStage.DeleteOld, FileId = "old-file-id" },
            Attempts = 0,
            CreatedAt = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero),
        }));

        var outbox = new UploadOutbox(outboxDir);

        var restored = outbox.Entries.Should().ContainSingle().Subject;
        restored.Stage.Should().Be(PersistStage.DeleteOld);
        restored.Op!.Stage.Should().Be(PersistStage.DeleteOld);
        restored.Op.Upload2.Should().BeNull();
    }

    [Fact]
    public void Outbox_reads_upload2_manifest_without_restart_limit_timestamp()
    {
        using var temp = new TestDirectory();
        var outboxDir = temp.Path("outbox");
        Directory.CreateDirectory(outboxDir);
        var id = Guid.NewGuid().ToString("D");
        var dataPath = Path.Combine(outboxDir, id + ".bin");
        File.WriteAllBytes(dataPath, [1]);
        File.WriteAllText(Path.Combine(outboxDir, id + ".json"), JsonSerializer.Serialize(new
        {
            Id = id,
            DataPath = dataPath,
            Name = "legacy-upload2.bin",
            ServerKey = "server",
            OwnerUserId = "owner",
            Stage = PersistStage.Upload,
            Op = new
            {
                Stage = PersistStage.Upload,
                Upload2 = new { IdempotencyKey = "key", SessionRestarts = 3 },
            },
        }));

        var restored = new UploadOutbox(outboxDir).Find(id)!;

        restored.Op!.Upload2!.SessionRestarts.Should().Be(3);
        restored.Op.Upload2.SessionRestartLimitReachedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(401, ServerErrors.UploadTokenInvalid)]
    [InlineData(404, ServerErrors.UploadSessionNotFound)]
    [InlineData(409, ServerErrors.UploadSessionState)]
    public async Task Part_error_resumes_and_uploads_missing_parts(int httpStatus, string code)
    {
        using var temp = new TestDirectory();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var cloud = new FakeCloud();
        var responder = cloud.HttpHandler.Responder;
        var puts = 0;
        cloud.HttpHandler.Responder = (request, token) => ++puts == 2
            ? Task.FromResult(PartError(httpStatus, code)) : responder(request, token);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        await gateway.UploadResumableAsync(new PersistOperation(), path, "retry.bin", () => { }, TimeSpan.FromMinutes(2));
        cloud.HttpHandler.Calls.Select(call => call.Url.Split('/').Last()).Should().Equal("1", "2", "2");
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ResumeUploadSession).Should().Be(2);
    }

    [Theory]
    [InlineData(401, ServerErrors.UploadTokenInvalid)]
    [InlineData(404, ServerErrors.UploadSessionNotFound)]
    [InlineData(409, ServerErrors.UploadSessionState)]
    public async Task Persistent_part_error_has_three_resume_retries(int httpStatus, string code)
    {
        using var temp = new TestDirectory();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1]);
        var cloud = new FakeCloud();
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(PartError(httpStatus, code));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var operation = new PersistOperation();
        var error = await FluentActions.Invoking(() => gateway.UploadResumableAsync(operation, path,
            "limit.bin", () => { }, TimeSpan.FromMinutes(2))).Should().ThrowAsync<InvalidOperationException>();
        error.Which.Message.Should().Contain($"HTTP {httpStatus}: {code}");
        cloud.HttpHandler.Calls.Should().HaveCount(4);
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ResumeUploadSession).Should().Be(4);
        operation.Upload2!.SessionRestarts.Should().Be(0);
    }

    [Theory]
    [InlineData(FakeCloudMethods.GetUploadSession)]
    [InlineData(FakeCloudMethods.ResumeUploadSession)]
    public async Task Missing_session_recreates_without_resuming_a_failed_get(string missingMethod)
    {
        using var temp = new TestDirectory();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var cloud = new FakeCloud { FailUploadPartNumberOnce = 2 };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var operation = new PersistOperation();
        await FluentActions.Invoking(() => gateway.UploadResumableAsync(operation, path, "missing.bin",
            () => { }, TimeSpan.FromMinutes(2))).Should().ThrowAsync<HttpRequestException>();
        var oldId = operation.Upload2!.SessionId;
        cloud.Invoker.FailNext(missingMethod, FakeCallInvoker.BusinessError(ServerErrors.UploadSessionNotFound));
        await gateway.UploadResumableAsync(operation, path, "missing.bin", () => { }, TimeSpan.FromMinutes(2));
        operation.Upload2.SessionId.Should().NotBe(oldId);
        operation.Upload2.SessionRestarts.Should().Be(1);
        var oldResumes = cloud.Invoker.Calls.Where(call => call.MethodFullName == FakeCloudMethods.ResumeUploadSession
            && ((UploadSessionIdRequest)call.Request).SessionId == oldId);
        oldResumes.Should().HaveCount(missingMethod == FakeCloudMethods.GetUploadSession ? 1 : 2);
    }

    [Theory]
    [InlineData(UploadSessionStatus.Failed)]
    [InlineData(UploadSessionStatus.Cancelled)]
    [InlineData(UploadSessionStatus.Expired)]
    public async Task Saved_terminal_session_is_recreated(UploadSessionStatus status)
    {
        using var temp = new TestDirectory();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var cloud = new FakeCloud { FailUploadPartNumberOnce = 2 };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var operation = new PersistOperation();
        await FluentActions.Invoking(() => gateway.UploadResumableAsync(operation, path, "terminal.bin",
            () => { }, TimeSpan.FromMinutes(2))).Should().ThrowAsync<HttpRequestException>();
        var oldId = operation.Upload2!.SessionId!;
        cloud.SetUploadSessionStatus(oldId, status);
        await gateway.UploadResumableAsync(operation, path, "terminal.bin", () => { }, TimeSpan.FromMinutes(2));
        operation.Upload2.SessionRestarts.Should().Be(1);
        operation.Upload2.SessionId.Should().NotBe(oldId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Three_terminal_outcomes_or_conflicts_persist_limit_across_worker_calls(bool conflict)
    {
        using var temp = new TestDirectory();
        var time = new FakeTimeProvider();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1]);
        var cloud = new FakeCloud();
        for (var i = 0; i < 3; i++)
            if (conflict)
                cloud.Invoker.FailNext(FakeCloudMethods.CreateUploadSession,
                    FakeCallInvoker.BusinessError(ServerErrors.UploadIdempotencyConflict));
            else
                cloud.UploadCompleteStatuses.Enqueue(UploadSessionStatus.Failed);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var operation = new PersistOperation();
        var outbox = new UploadOutbox(temp.Path("outbox"), time);
        var entry = new UploadOutboxEntry { Id = "limit", DataPath = path, Op = operation };
        await FluentActions.Invoking(() => gateway.UploadResumableAsync(operation, path, "limit.bin",
            () => outbox.Save(entry), TimeSpan.FromMinutes(2))).Should().ThrowAsync<InvalidOperationException>();
        var restored = new UploadOutbox(outbox.DirectoryPath).Find(entry.Id)!;
        restored.Op!.Upload2!.SessionRestarts.Should().Be(3);
        restored.Op.Upload2.SessionRestartLimitReachedAtUtc.Should().Be(time.GetUtcNow());
        var creates = cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession);
        creates.Should().Be(3);
        for (var i = 0; i < 2; i++)
            await FluentActions.Invoking(() => gateway.UploadResumableAsync(restored.Op, path, "limit.bin",
                () => { }, TimeSpan.FromMinutes(2))).Should().ThrowAsync<InvalidOperationException>();
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession).Should().Be(creates);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outbox.DirectoryPath, "limit.json")));
        manifest.RootElement.GetProperty("Op").GetProperty("Upload2")
            .GetProperty("SessionRestartLimitReachedAtUtc").GetDateTimeOffset().Should().Be(time.GetUtcNow());
    }

    [Fact]
    public async Task Three_terminal_outcomes_resume_after_one_hour()
    {
        using var temp = new TestDirectory();
        var time = new FakeTimeProvider();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1]);
        var cloud = new FakeCloud();
        for (var i = 0; i < 3; i++)
            cloud.UploadCompleteStatuses.Enqueue(UploadSessionStatus.Failed);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var outbox = new UploadOutbox(temp.Path("outbox"), time);
        var entry = new UploadOutboxEntry { Id = "cooldown", DataPath = path, Name = "cooldown.bin",
            ServerKey = "server", OwnerUserId = "owner", Op = new PersistOperation() };

        await FluentActions.Invoking(() => gateway.UploadResumableAsync(entry.Op!, path, entry.Name,
                () => outbox.Save(entry), TimeSpan.FromMinutes(2)))
            .Should().ThrowAsync<InvalidOperationException>();
        var restored = new UploadOutbox(outbox.DirectoryPath).Find(entry.Id)!;
        var creates = cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession);

        time.Advance(TimeSpan.FromHours(1));
        await gateway.UploadResumableAsync(restored.Op!, path, entry.Name,
            () => outbox.Save(restored), TimeSpan.FromMinutes(2));

        restored.Op!.Upload2!.SessionRestarts.Should().Be(0);
        restored.Op.Upload2.SessionRestartLimitReachedAtUtc.Should().BeNull();
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession)
            .Should().Be(creates + 1);
    }

    [Fact]
    public async Task Future_restart_limit_timestamp_is_treated_as_expired()
    {
        using var temp = new TestDirectory();
        var time = new FakeTimeProvider();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1]);
        var cloud = new FakeCloud();
        for (var i = 0; i < 3; i++)
            cloud.UploadCompleteStatuses.Enqueue(UploadSessionStatus.Failed);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var operation = new PersistOperation();

        await FluentActions.Invoking(() => gateway.UploadResumableAsync(operation, path, "future-limit.bin",
                () => { }, TimeSpan.FromMinutes(2)))
            .Should().ThrowAsync<InvalidOperationException>();
        operation.Upload2!.SessionRestarts.Should().Be(3);
        operation.Upload2.SessionRestartLimitReachedAtUtc = time.GetUtcNow().AddDays(1);
        var creates = cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession);

        await gateway.UploadResumableAsync(operation, path, "future-limit.bin", () => { }, TimeSpan.FromMinutes(2));

        operation.Upload2.SessionRestarts.Should().Be(0);
        operation.Upload2.SessionRestartLimitReachedAtUtc.Should().BeNull();
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession)
            .Should().Be(creates + 1);
    }

    [Theory]
    [InlineData(UploadSessionStatus.Processing)]
    [InlineData(UploadSessionStatus.Ready)]
    [InlineData(UploadSessionStatus.Failed)]
    public async Task Part_state_error_follows_resumed_status(UploadSessionStatus status)
    {
        using var temp = new TestDirectory();
        var time = new FakeTimeProvider();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1]);
        var cloud = new FakeCloud();
        var responder = cloud.HttpHandler.Responder;
        var first = true;
        cloud.HttpHandler.Responder = (request, token) =>
        {
            if (!first)
                return responder(request, token);
            first = false;
            var sessionId = request.RequestUri!.Segments[^3].Trim('/');
            cloud.SetUploadSessionStatus(sessionId, status);
            return Task.FromResult(PartError(409, ServerErrors.UploadSessionState));
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResumableProcessingWaitStartedForTesting = _ => waiting.TrySetResult();
        var operation = new PersistOperation();
        var upload = gateway.UploadResumableAsync(operation, path, "state.bin", () => { }, TimeSpan.FromMinutes(2));
        if (status == UploadSessionStatus.Processing)
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            time.Advance(TimeSpan.FromMinutes(2));
            await FluentActions.Invoking(() => upload).Should().ThrowAsync<UploadProcessingPendingException>();
            operation.Stage.Should().Be(PersistStage.Processing);
        }
        else
            (await upload).Should().Be(operation.FileId);
        operation.Upload2!.SessionRestarts.Should().Be(status == UploadSessionStatus.Failed ? 1 : 0);
    }

    [Fact]
    public async Task Processing_poll_does_not_save_unchanged_state()
    {
        using var temp = new TestDirectory();
        var time = new FakeTimeProvider();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1]);
        var cloud = new FakeCloud();
        cloud.UploadCompleteStatuses.Enqueue(UploadSessionStatus.Processing);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var waits = new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true };
        var signals = System.Threading.Channels.Channel.CreateUnbounded<bool>(waits);
        gateway.ResumableProcessingWaitStartedForTesting = _ => signals.Writer.TryWrite(true);
        var saves = 0;
        var operation = new PersistOperation();
        var upload = gateway.UploadResumableAsync(operation, path, "poll.bin", () => saves++, TimeSpan.FromMinutes(2));
        await signals.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var initialSaves = saves;
        time.Advance(TimeSpan.FromMilliseconds(250));
        await signals.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        saves.Should().Be(initialSaves);
        cloud.SetUploadSessionStatus(operation.Upload2!.SessionId!, UploadSessionStatus.Ready);
        time.Advance(TimeSpan.FromMilliseconds(500));
        await upload.WaitAsync(TimeSpan.FromSeconds(10));
        saves.Should().Be(initialSaves);
    }

    [Fact]
    public async Task Processing_poll_intervals_back_off_to_two_seconds()
    {
        using var temp = new TestDirectory();
        var time = new FakeTimeProvider();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1]);
        var cloud = new FakeCloud();
        cloud.UploadCompleteStatuses.Enqueue(UploadSessionStatus.Processing);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var intervals = System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        gateway.ResumableProcessingWaitStartedForTesting = interval => intervals.Writer.TryWrite(interval);
        var operation = new PersistOperation();
        var upload = gateway.UploadResumableAsync(operation, path, "poll-backoff.bin", () => { },
            TimeSpan.FromMinutes(2));
        var expectedIntervals = new[]
        {
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
        };

        for (var index = 0; index < expectedIntervals.Length; index++)
        {
            var expectedInterval = expectedIntervals[index];
            var interval = await intervals.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            interval.Should().Be(expectedInterval);
            if (index == expectedIntervals.Length - 1)
                cloud.SetUploadSessionStatus(operation.Upload2!.SessionId!, UploadSessionStatus.Ready);
            time.Advance(interval);
        }

        (await upload.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(operation.FileId);
    }

    [Fact]
    public async Task Processing_ready_on_first_poll_waits_only_250_milliseconds()
    {
        using var temp = new TestDirectory();
        var time = new FakeTimeProvider();
        var start = time.GetTimestamp();
        var path = temp.Path("data.bin");
        await File.WriteAllBytesAsync(path, [1]);
        var cloud = new FakeCloud();
        cloud.UploadCompleteStatuses.Enqueue(UploadSessionStatus.Processing);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var firstPoll = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResumableProcessingWaitStartedForTesting = interval => firstPoll.TrySetResult(interval);
        var operation = new PersistOperation();
        var upload = gateway.UploadResumableAsync(operation, path, "poll-ready.bin", () => { },
            TimeSpan.FromMinutes(2));

        var firstInterval = await firstPoll.Task.WaitAsync(TimeSpan.FromSeconds(10));
        firstInterval.Should().Be(TimeSpan.FromMilliseconds(250));
        cloud.SetUploadSessionStatus(operation.Upload2!.SessionId!, UploadSessionStatus.Ready);
        time.Advance(firstInterval);

        (await upload.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(operation.FileId);
        time.GetElapsedTime(start).Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Theory]
    [InlineData(ServerErrors.UploadSessionState)]
    [InlineData(ServerErrors.UploadSessionNotFound)]
    public async Task Modification_resets_upload2_and_swallows_cancel_error(string errorCode)
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\modified.bin", [1]);
        var session = new FilesApi.FilesApiClient(cloud.Invoker).CreateUploadSession(new CreateUploadSessionRequest
        { IdempotencyKey = "old", FileName = "modified.bin", FileSize = 1 });
        local.Op = new PersistOperation { Upload2 = new Upload2State { SessionId = session.SessionId, SessionRestarts = 2 } };
        cloud.Invoker.FailNext(FakeCloudMethods.CancelUploadSession,
            FakeCallInvoker.BusinessError(errorCode));
        fileSystem.WriteFile("\\modified.bin", [2], out _, 0, info).Should().Be(DokanResult.Success);
        local.Op.Upload2.Should().BeNull();
        local.Op.Stage.Should().Be(PersistStage.Upload);
        await fileSystem.UploadCancellationTaskForTesting!.WaitAsync(TimeSpan.FromSeconds(10));
        fileSystem.LastSyncError.Should().BeNullOrEmpty();
        fileSystem.Cleanup("\\modified.bin", info);
        fileSystem.CloseFile("\\modified.bin", info);
        local.Op.Upload2!.SessionRestarts.Should().Be(0);
    }

    [Fact]
    public async Task Deleting_writable_with_upload_session_cancels_it_in_background()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var (info, local) = OpenWritableAndWrite(fileSystem, "\\delete-upload.bin", [1]);
        var session = new FilesApi.FilesApiClient(cloud.Invoker).CreateUploadSession(new CreateUploadSessionRequest
        { IdempotencyKey = "delete-upload", FileName = "delete-upload.bin", FileSize = 1 });
        local.Op = new PersistOperation
        {
            Upload2 = new Upload2State { IdempotencyKey = "delete-upload", Name = "delete-upload.bin",
                Size = 1, SessionId = session.SessionId },
        };
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.Invoker.OnCall = method =>
        {
            if (method == FakeCloudMethods.CancelUploadSession)
                cancelled.TrySetResult();
        };
        info.DeletePending = true;

        fileSystem.Cleanup("\\delete-upload.bin", info);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fileSystem.CloseFile("\\delete-upload.bin", info);

        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.CancelUploadSession).Should().Be(1);
    }

    [Fact]
    public async Task Missing_upload_origin_fails_before_create_or_http()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var http = new HttpClient(cloud.HttpHandler);
        using var gateway = new CloudGateway(new CloudApi.CloudApiClient(cloud.Invoker),
            new FilesApi.FilesApiClient(cloud.Invoker), http, "https://files.test:7025/web",
            temp.Path("cache"));
        await FluentActions.Invoking(() => gateway.UploadResumableAsync(new PersistOperation(),
            temp.Path("missing.bin"), "missing.bin", () => { }, TimeSpan.FromMinutes(2)))
            .Should().ThrowAsync<InvalidOperationException>();
        cloud.Invoker.Calls.Should().BeEmpty();
        cloud.HttpHandler.Calls.Should().BeEmpty();
    }

    private static HttpResponseMessage PartError(int httpStatus, string code)
        => new((HttpStatusCode)httpStatus) { Content = new StringContent(JsonSerializer.Serialize(new { code })) };

    private static (FakeDokanFileInfo Info, LocalFile File) OpenWritableAndWrite(
        BarkCloudFileSystem fileSystem, string path, byte[] content)
    {
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile(path, FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        var file = ((HandleContext)info.Context!).File!;
        fileSystem.WriteFile(path, content, out var bytesWritten, 0, info).Should().Be(DokanResult.Success);
        bytesWritten.Should().Be(content.Length);
        return (info, file);
    }
}
