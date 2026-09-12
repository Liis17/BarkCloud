namespace BarkCloud.Files.Services;

public interface IUploadTempFileProvider
{
    string CreatePath();
}

public sealed class UploadTempFileProvider(IConfiguration configuration) : IUploadTempFileProvider
{
    public string CreatePath()
    {
        var configured = configuration["Uploads:TempDirectory"];
        var directory = string.IsNullOrWhiteSpace(configured) ? Path.GetTempPath() : configured;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"barkcloud-upload-{Guid.NewGuid():N}.tmp");
    }
}
