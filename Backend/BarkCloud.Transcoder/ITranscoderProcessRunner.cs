namespace BarkCloud.Transcoder;

public interface ITranscoderProcessRunner
{
    Task RunAsync(
        string inputPath,
        string outputPath,
        int height,
        Action<int> reportProgress,
        CancellationToken cancellationToken);
}
