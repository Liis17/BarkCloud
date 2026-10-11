using System.Net;

using BarkCloud.Drive.Engine.Tests.Fakes;

using FluentAssertions;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class CloudGatewayCacheEpochTests
{
    [Fact]
    public async Task Download_finishing_after_clear_is_not_published_to_disk_cache()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "cache-epoch.bin", content);
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.HttpHandler.Responder = async (request, _) =>
        {
            if (request.Headers.Range is not null)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content),
                };

            downloadStarted.TrySetResult();
            await releaseDownload.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
        };
        var cacheDir = temp.Path("cache");
        var fileId = file.FileId;
        using var gateway = cloud.CreateGateway(cacheDir);

        var read = Task.Factory.StartNew(() => gateway.Read(fileId, content.Length,
                new byte[content.Length], 0), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gateway.ClearCache();
        releaseDownload.TrySetResult();

        var act = async () => await read.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(cacheDir, fileId)).Should().BeFalse();
        Directory.Exists(Path.Combine(cacheDir, fileId + ".blocks")).Should().BeFalse();
        Directory.GetFiles(cacheDir).Should().BeEmpty();
    }
}
