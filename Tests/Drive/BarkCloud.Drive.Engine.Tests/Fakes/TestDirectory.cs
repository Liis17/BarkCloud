namespace BarkCloud.Drive.Engine.Tests.Fakes;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BarkCloudDriveTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(params string[] segments) => System.IO.Path.Combine(new[] { Root }.Concat(segments).ToArray());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
