using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

using BarkCloud.Files.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public sealed class SvgRasterizerTests
{
    private const string ValidSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"12\" height=\"8\" />";

    [Fact]
    public async Task RasterizeUpload_UsesPrlimitAndRsvgWithCleanBoundedEnvironmentAndExactInput()
    {
        SvgProcessRequest? request = null;
        var runner = new StubSvgProcessRunner((captured, _) =>
        {
            request = captured;
            Directory.Exists(captured.Environment["HOME"]).Should().BeTrue();
            Directory.Exists(captured.Environment["XDG_CACHE_HOME"]).Should().BeTrue();
            return Task.FromResult(Png(12, 8));
        });
        var rasterizer = CreateSut(runner);
        var input = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"bad()\"><script>bad()</script></svg>");
        using var source = new MemoryStream(input, writable: false);

        var result = await rasterizer.RasterizeUploadAsync(source, "safe.svg", default);

        result.Should().BeEquivalentTo(Png(12, 8));
        source.Position.Should().Be(0);
        request.Should().NotBeNull();
        request!.FileName.Should().Be("/usr/bin/prlimit");
        request.Arguments.Should().Equal(
            "--as=1073741824", "--", "/usr/bin/rsvg-convert", "--format=png",
            "--keep-aspect-ratio", "--width=2500", "--height=2500", "-");
        request.Arguments.Should().NotContain(arg => arg.StartsWith("--output", StringComparison.Ordinal));
        request.Arguments.Should().NotContain("--unlimited");
        request.InputBytes.Should().BeEquivalentTo(input);
        request.MaximumOutputBytes.Should().Be(32 * 1024 * 1024);
        request.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        request.Environment["RAYON_NUM_THREADS"].Should().Be("1");
        request.Environment.Keys.Should().BeEquivalentTo("PATH", "RAYON_NUM_THREADS", "HOME", "XDG_CACHE_HOME", "TMPDIR", "LANG", "LC_ALL");
        Path.GetDirectoryName(request.Environment["HOME"]).Should().NotBeNull();
        Directory.Exists(Path.GetDirectoryName(request.Environment["HOME"])!).Should().BeFalse();
    }

    [Theory]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><svg>&x;</svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" xml:base=\"file:///tmp\" />")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xi=\"http://www.w3.org/2001/XInclude\"><xi:include href=\"file:///etc/passwd\" /></svg>")]
    [InlineData("<html />")]
    public async Task Rasterize_InvalidOrExternalXml_IsRejectedBeforeStartingProcess(string xml)
    {
        var runner = new StubSvgProcessRunner((_, _) => Task.FromResult(Png(1, 1)));
        var rasterizer = CreateSut(runner);

        var result = await rasterizer.RasterizeUploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "bad.svg", default);

        result.Should().BeNull();
        runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Rasterize_SvgzAndOversizedInput_AreRejectedWithoutStartingProcess()
    {
        var runner = new StubSvgProcessRunner((_, _) => Task.FromResult(Png(1, 1)));
        var rasterizer = CreateSut(runner);

        var compressed = await rasterizer.RasterizeUploadAsync(new MemoryStream([0x1f, 0x8b]), "image.svg", default);
        var namedSvgz = await rasterizer.RasterizeUploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(ValidSvg)), "image.svgz", default);
        var oversized = await rasterizer.RasterizeUploadAsync(
            new MemoryStream(new byte[SvgRasterizer.MaximumInputBytes + 1]), "large.svg", default);

        compressed.Should().BeNull();
        namedSvgz.Should().BeNull();
        oversized.Should().BeNull();
        runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Rasterize_RejectsPngOutsideConfiguredBounds()
    {
        var runner = new StubSvgProcessRunner((_, _) => Task.FromResult(Png(2501, 1)));
        var result = await CreateSut(runner).RasterizeUploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(ValidSvg)), "image.svg", default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Rasterize_RejectsPngOver32Mib()
    {
        var oversizedPng = new byte[SvgRasterizer.MaximumPngBytes + 1];
        var runner = new StubSvgProcessRunner((_, _) => Task.FromResult(oversizedPng));
        var result = await CreateSut(runner).RasterizeUploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(ValidSvg)), "image.svg", default);

        result.Should().BeNull();
        runner.Requests.Single().MaximumOutputBytes.Should().Be(SvgRasterizer.MaximumPngBytes);
    }

    [Fact]
    public async Task Rasterize_ProcessTimeoutReturnsNoPreview()
    {
        var runner = new StubSvgProcessRunner((_, _) => throw new OperationCanceledException("renderer timeout"));
        var result = await CreateSut(runner).RasterizeUploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(ValidSvg)), "image.svg", default);

        result.Should().BeNull();
        runner.Requests.Single().Timeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rasterize_CallerCancellationPropagates()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new StubSvgProcessRunner(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Png(1, 1);
        });
        var rasterizer = CreateSut(runner);
        using var cancellation = new CancellationTokenSource();
        var operation = rasterizer.RasterizeUploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(ValidSvg)), "image.svg", cancellation.Token);

        await started.Task;
        cancellation.Cancel();

        Func<Task> act = async () => await operation;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Rasterize_QueuedUploadRunsBeforeWaitingBackfill()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<SvgProcessRequest>();
        var runner = new StubSvgProcessRunner(async (request, cancellationToken) =>
        {
            lock (requests)
                requests.Add(request);
            if (requests.Count == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            return Png(1, 1);
        });
        var rasterizer = CreateSut(runner);

        var firstBackfill = rasterizer.RasterizeBackfillAsync(SvgWithId("first"), "first.svg", default);
        await firstStarted.Task;
        var queuedBackfill = rasterizer.RasterizeBackfillAsync(SvgWithId("backfill"), "backfill.svg", default);
        var queuedUpload = rasterizer.RasterizeUploadAsync(SvgWithId("upload"), "upload.svg", default);
        releaseFirst.TrySetResult();

        (await firstBackfill).Should().NotBeNull();
        (await queuedUpload).Should().NotBeNull();
        (await queuedBackfill).Should().NotBeNull();
        lock (requests)
        {
            Encoding.UTF8.GetString(requests[1].InputBytes).Should().Contain("id=\"upload\"");
            Encoding.UTF8.GetString(requests[2].InputBytes).Should().Contain("id=\"backfill\"");
        }
    }

    [Fact]
    public async Task Rasterize_UploadReturnsWithoutPreviewWhenSlotIsBusyFor250Milliseconds()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new StubSvgProcessRunner(async (_, cancellationToken) =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            return Png(1, 1);
        });
        var rasterizer = CreateSut(runner);
        var backfill = rasterizer.RasterizeBackfillAsync(SvgWithId("first"), "first.svg", default);
        await firstStarted.Task;

        var result = await rasterizer.RasterizeUploadAsync(SvgWithId("upload"), "upload.svg", default);

        result.Should().BeNull();
        releaseFirst.TrySetResult();
        (await backfill).Should().NotBeNull();
        runner.Requests.Should().HaveCount(1);
    }

    private static SvgRasterizer CreateSut(ISvgProcessRunner runner) =>
        new(runner, NullLogger<SvgRasterizer>.Instance);

    private static MemoryStream SvgWithId(string id) =>
        new(Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" id=\"{id}\" />"));

    private static byte[] Png(uint width, uint height)
    {
        var png = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(png, 12);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), height);
        return png;
    }

    private sealed class StubSvgProcessRunner(
        Func<SvgProcessRequest, CancellationToken, Task<byte[]>> run) : ISvgProcessRunner
    {
        public List<SvgProcessRequest> Requests { get; } = [];

        public Task<byte[]> RunAsync(SvgProcessRequest request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request);
            return run(request, cancellationToken);
        }
    }
}
