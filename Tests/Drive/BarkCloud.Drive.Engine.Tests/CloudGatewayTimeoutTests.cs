using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using FluentAssertions;
using Grpc.Core;

using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class CloudGatewayTimeoutTests
{
    [Fact]
    public async Task Upload_waits_for_server_response_after_body_is_sent()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider();
        var bodySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            bodySent.TrySetResult();
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"fileId\":\"uploaded\"}", Encoding.UTF8, "application/json"),
            };
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var source = temp.Path("upload.bin");
        File.WriteAllBytes(source, [1, 2, 3]);

        var upload = gateway.UploadAsync("https://files.test/upload", "upload.bin", source);
        await bodySent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(61));
        releaseResponse.TrySetResult();

        (await upload.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("uploaded");
    }

    [Fact]
    public async Task Upload_times_out_if_server_does_not_respond_after_body_is_sent()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider();
        var bodySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            bodySent.TrySetResult();
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var source = temp.Path("upload.bin");
        File.WriteAllBytes(source, [1, 2, 3]);

        var upload = gateway.UploadAsync("https://files.test/upload", "upload.bin", source);
        await bodySent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMinutes(10));

        var error = await FluentActions.Awaiting(() => upload).Should().ThrowAsync<TimeoutException>();
        error.Which.Message.Should().Be(Loc.T("Eng_HttpTransferTimedOut"));
    }

    [Fact]
    public async Task Download_throws_timeout_when_no_bytes_arrive_for_idle_window()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var fileId = cloud.AddUnattachedFile([1]);
        var time = new FakeTimeProvider();
        var stream = new ControlledReadStream();
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        });
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        var downloadTask = gateway.DownloadToAsync(fileId, temp.Path("download.bin"), 1);
        await stream.WaitForReadAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(60));

        await FluentActions.Awaiting(() => downloadTask)
            .Should().ThrowAsync<TimeoutException>()
            .WithMessage(Loc.T("Eng_HttpTransferTimedOut"));
        File.Exists(temp.Path("download.bin")).Should().BeFalse();
        Directory.GetFiles(temp.Path(), "download.bin.*.part").Should().BeEmpty();
    }

    [Fact]
    public async Task Range_block_times_out_after_60_seconds_even_when_bytes_keep_arriving()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var fileId = cloud.AddUnattachedFile([1, 2]);
        var time = new FakeTimeProvider();
        var stream = new ControlledReadStream();
        cloud.HttpHandler.Responder = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 1, 2);
            return Task.FromResult(response);
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        var read = Task.Run(() => gateway.Read(fileId, 2, new byte[2], 0));
        await stream.WaitForReadAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(59));
        stream.Push([1]);
        await stream.WaitForReadAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(2));

        var error = await FluentActions.Awaiting(() => read).Should().ThrowAsync<TimeoutException>();
        error.Which.Message.Should().Be(Loc.T("Eng_HttpTransferTimedOut"));
    }

    [Fact]
    public async Task Range_block_translates_cancelled_temp_url_RPC_after_its_deadline()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var fileId = cloud.AddUnattachedFile([1]);
        var time = new FakeTimeProvider();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRequest = new ManualResetEventSlim();
        cloud.Invoker.Handle<GetTempDownloadUrlRequest, GetTempDownloadUrlResponse>(
            FakeCloudMethods.GetTempDownloadUrl, _ =>
            {
                requestStarted.TrySetResult();
                releaseRequest.Wait();
                throw new RpcException(new Status(StatusCode.Cancelled, "cancelled"));
            });
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        var read = Task.Run(() => gateway.Read(fileId, 1, new byte[1], 0));
        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(60));
        }
        finally
        {
            releaseRequest.Set();
        }

        await FluentActions.Awaiting(() => read)
            .Should().ThrowAsync<TimeoutException>()
            .WithMessage(Loc.T("Eng_HttpTransferTimedOut"));
    }

    [Fact]
    public void Range_block_preserves_cancelled_temp_url_RPC_before_its_deadline()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var fileId = cloud.AddUnattachedFile([1]);
        cloud.Invoker.FailNext(FakeCloudMethods.GetTempDownloadUrl,
            new RpcException(new Status(StatusCode.Cancelled, "cancelled")));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());

        Action read = () => gateway.Read(fileId, 1, new byte[1], 0);

        read.Should().Throw<RpcException>()
            .Which.StatusCode.Should().Be(StatusCode.Cancelled);
    }

    [Fact]
    public async Task Download_does_not_cancel_while_bytes_keep_arriving()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var fileId = cloud.AddUnattachedFile([1, 2]);
        var time = new FakeTimeProvider();
        var stream = new ControlledReadStream();
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        });
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        var download = gateway.DownloadToAsync(fileId, temp.Path("download.bin"), 2);
        await stream.WaitForReadAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(59));
        stream.Push([1]);
        await stream.WaitForReadAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(59));
        stream.Push([2]);
        await stream.WaitForReadAsync(3).WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(59));
        stream.Push(null);
        await download.WaitAsync(TimeSpan.FromSeconds(5));

        File.ReadAllBytes(temp.Path("download.bin")).Should().Equal([1, 2]);
    }

    private sealed class ControlledReadStream : Stream
    {
        private readonly Channel<byte[]?> _chunks = Channel.CreateUnbounded<byte[]?>();
        private readonly object _readLock = new();
        private readonly Dictionary<int, TaskCompletionSource> _readWaiters = new();
        private int _readCount;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public Task WaitForReadAsync(int count)
        {
            lock (_readLock)
            {
                if (_readCount >= count)
                    return Task.CompletedTask;
                if (!_readWaiters.TryGetValue(count, out var waiter))
                    _readWaiters.Add(count, waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                return waiter.Task;
            }
        }

        public void Push(byte[]? chunk) => _chunks.Writer.TryWrite(chunk);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource? waiter;
            lock (_readLock)
            {
                _readCount++;
                _readWaiters.TryGetValue(_readCount, out waiter);
            }
            waiter?.TrySetResult();

            var chunk = await _chunks.Reader.ReadAsync(cancellationToken);
            if (chunk is null)
                return 0;
            chunk.AsMemory().CopyTo(buffer);
            return chunk.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
