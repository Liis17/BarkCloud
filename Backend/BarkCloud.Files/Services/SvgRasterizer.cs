using System.Buffers.Binary;
using System.Xml;

namespace BarkCloud.Files.Services;

public interface ISvgRasterizer
{
    Task<byte[]?> RasterizeUploadAsync(Stream source, string fileName, CancellationToken cancellationToken);
    Task<byte[]?> RasterizeBackfillAsync(Stream source, string fileName, CancellationToken cancellationToken);
}

/// <summary>Безопасно растрирует SVG в PNG для превью облачных файлов.</summary>
public sealed class SvgRasterizer : ISvgRasterizer
{
    public const int MaximumInputBytes = 2 * 1024 * 1024;
    public const int MaximumPngBytes = 32 * 1024 * 1024;
    public const int MaximumDimension = 2500;
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan UploadWaitTimeout = TimeSpan.FromMilliseconds(250);

    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string XIncludeNamespace = "http://www.w3.org/2001/XInclude";
    private const string LegacyXIncludeNamespace = "http://www.w3.org/2003/XInclude";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private readonly ISvgProcessRunner _processRunner;
    private readonly ILogger<SvgRasterizer> _logger;
    private readonly SvgRenderScheduler _scheduler = new();

    public SvgRasterizer(ISvgProcessRunner processRunner, ILogger<SvgRasterizer> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public Task<byte[]?> RasterizeUploadAsync(Stream source, string fileName, CancellationToken cancellationToken) =>
        RasterizeAsync(source, fileName, isUpload: true, cancellationToken);

    public Task<byte[]?> RasterizeBackfillAsync(Stream source, string fileName, CancellationToken cancellationToken) =>
        RasterizeAsync(source, fileName, isUpload: false, cancellationToken);

    private async Task<byte[]?> RasterizeAsync(
        Stream source,
        string fileName,
        bool isUpload,
        CancellationToken cancellationToken)
    {
        if (Path.GetExtension(fileName).Equals(".svgz", StringComparison.OrdinalIgnoreCase))
            return null;

        byte[]? input;
        try
        {
            input = await ReadBoundedAsync(source, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        if (input is null || IsGzip(input) || !IsSafeSvgXml(input))
            return null;

        IDisposable? lease;
        try
        {
            lease = isUpload
                ? await _scheduler.AcquireUploadAsync(UploadWaitTimeout, cancellationToken)
                : await _scheduler.AcquireBackfillAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (lease is null)
            return null;

        using (lease)
        {
            string? workDirectory = null;
            try
            {
                workDirectory = CreateWorkDirectory();
                var home = Path.Combine(workDirectory, "home");
                var cache = Path.Combine(workDirectory, "cache");
                Directory.CreateDirectory(home);
                Directory.CreateDirectory(cache);

                var request = new SvgProcessRequest(
                    FileName: "/usr/bin/prlimit",
                    Arguments:
                    [
                        "--as=1073741824",
                        "--",
                        "/usr/bin/rsvg-convert",
                        "--format=png",
                        "--keep-aspect-ratio",
                        $"--width={MaximumDimension}",
                        $"--height={MaximumDimension}",
                        "-",
                    ],
                    Environment: new Dictionary<string, string>
                    {
                        ["PATH"] = "/usr/bin:/bin",
                        ["RAYON_NUM_THREADS"] = "1",
                        ["HOME"] = home,
                        ["XDG_CACHE_HOME"] = cache,
                        ["TMPDIR"] = workDirectory,
                        ["LANG"] = "C.UTF-8",
                        ["LC_ALL"] = "C.UTF-8",
                    },
                    InputBytes: input,
                    MaximumOutputBytes: MaximumPngBytes,
                    Timeout: ProcessTimeout);

                var png = await _processRunner.RunAsync(request, cancellationToken);
                return IsBoundedPng(png) ? png : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось растрировать SVG {FileName}; файл сохранится без превью", fileName);
                return null;
            }
            finally
            {
                if (workDirectory is not null)
                    DeleteWorkDirectory(workDirectory);
            }
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        var startPosition = source.CanSeek ? source.Position : (long?)null;
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var remaining = MaximumInputBytes - (int)buffer.Length;
                var read = await source.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining + 1)), cancellationToken);
                if (read == 0)
                    return buffer.ToArray();
                if (read > remaining)
                    throw new InvalidDataException("SVG превысил лимит размера входного файла.");

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            if (startPosition is { } position)
                source.Position = position;
        }
    }

    private static bool IsSafeSvgXml(byte[] input)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumInputBytes,
            IgnoreComments = true,
        };

        try
        {
            using var source = new MemoryStream(input, writable: false);
            using var reader = XmlReader.Create(source, settings);
            var sawRoot = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.NamespaceURI is XIncludeNamespace or LegacyXIncludeNamespace)
                        return false;
                    if (!sawRoot)
                    {
                        if (!reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase)
                            || (reader.NamespaceURI.Length > 0 && reader.NamespaceURI != SvgNamespace))
                            return false;
                        sawRoot = true;
                    }

                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            if (reader.NamespaceURI == XmlNamespace && reader.LocalName == "base")
                                return false;
                        }
                        reader.MoveToElement();
                    }
                }
            }

            return sawRoot;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool IsGzip(byte[] input) => input.Length >= 2 && input[0] == 0x1f && input[1] == 0x8b;

    private static bool IsBoundedPng(byte[] png)
    {
        if (png.Length is < 24 or > MaximumPngBytes || !png.AsSpan(0, 8).SequenceEqual(PngSignature)
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return false;

        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        return width is > 0 and <= MaximumDimension && height is > 0 and <= MaximumDimension;
    }

    private static string CreateWorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"barkcloud-svg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private void DeleteWorkDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить временную директорию SVG-рендерера {Path}", path);
        }
    }
}
