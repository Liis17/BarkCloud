namespace BarkCloud.Files.Configurations;

public sealed class VideoVariantOptions
{
    public bool Enabled { get; set; }

    public int? MaxSourceSizeMb { get; set; }

    public int? MaxDurationMinutes { get; set; }

    public string WorkDirectory { get; set; } = "/mnt/transcode";

    public string TranscoderUrl { get; set; } = "http://cloud-transcoder:8080";
}
