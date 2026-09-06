using System.Text.RegularExpressions;

namespace BarkCloud.Web.Infrastructure;

/// <summary>Образ BarkCloud-сервиса, найденный в docker-compose.yml.</summary>
public sealed record ComposeImageInfo(string Service, string BaseRepository, string Branch, string Tag, int LineIndex);

/// <summary>
/// Читает и точечно меняет image-строки BarkCloud в Compose-файле. Запись выполняется
/// в тот же файл и inode, потому что он смонтирован в web-контейнер как bind mount.
/// </summary>
public sealed class ComposeImageService
{
    public const string DefaultComposeFilePath = "/docker-compose.yml";
    public const string DefaultBackupDirectory = "/app/maintenance/compose-backups";
    private const string RegistryPrefix = DockerRegistryService.RegistryHost + "/";
    private const int BackupsToKeep = 20;

    public static readonly string[] Branches = ["master", "nightly", "dev"];

    private static readonly Regex TopLevelSectionRegex = new(
        @"^(?<name>[A-Za-z0-9._-]+):",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ServiceHeaderRegex = new(
        @"^  (?<name>[A-Za-z0-9._-]+):\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ImageRegex = new(
        @"^\s+image:\s*[""']?docker\.barkfluff\.com/(?<base>barkcloud-[a-z0-9-]+?)(?<suffix>-nightly|-dev)?:(?<tag>[^\s""']+)[""']?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    private readonly ILogger<ComposeImageService> _logger;
    private readonly string _composeFilePath;
    private readonly string _backupDirectory;

    public ComposeImageService(IConfiguration configuration, ILogger<ComposeImageService> logger)
    {
        _logger = logger;
        _composeFilePath = configuration["Docker:ComposeFile"] ?? DefaultComposeFilePath;
        _backupDirectory = configuration["Docker:ComposeBackupDirectory"] ?? DefaultBackupDirectory;
    }

    public string BackupDirectory => _backupDirectory;

    public static IReadOnlyDictionary<string, ComposeImageInfo> Parse(string composeYaml)
    {
        var result = new Dictionary<string, ComposeImageInfo>(StringComparer.OrdinalIgnoreCase);
        var lines = SplitLines(composeYaml);
        var inServices = false;
        string? currentService = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            if (TopLevelSectionRegex.IsMatch(text))
            {
                inServices = text.StartsWith("services:", StringComparison.Ordinal);
                currentService = null;
                continue;
            }

            if (!inServices)
                continue;

            var header = ServiceHeaderRegex.Match(text);
            if (header.Success)
            {
                currentService = header.Groups["name"].Value;
                continue;
            }

            if (currentService is null || result.ContainsKey(currentService))
                continue;

            var image = ImageRegex.Match(text);
            if (!image.Success)
                continue;

            result[currentService] = new ComposeImageInfo(
                currentService,
                image.Groups["base"].Value,
                BranchFromSuffix(image.Groups["suffix"].Value),
                image.Groups["tag"].Value,
                i);
        }

        return result;
    }

    public static bool TryRewrite(string composeYaml, string service, string branch, out string result, out string? error)
    {
        result = composeYaml;
        if (!IsKnownBranch(branch))
        {
            error = $"Неизвестный канал {branch}";
            return false;
        }

        var images = Parse(composeYaml);
        if (!images.TryGetValue(service, out var info))
        {
            error = $"Сервис {service} не найден в docker-compose.yml или его образ не из {RegistryPrefix}";
            return false;
        }

        error = null;
        if (string.Equals(info.Branch, branch, StringComparison.OrdinalIgnoreCase))
            return true;

        var lines = SplitLines(composeYaml);
        var oldReference = $"{RegistryPrefix}{DockerRegistryService.RepositoryForBranch(info.BaseRepository, info.Branch)}:";
        var newReference = $"{RegistryPrefix}{DockerRegistryService.RepositoryForBranch(info.BaseRepository, branch)}:";
        lines[info.LineIndex] = lines[info.LineIndex] with
        {
            Text = lines[info.LineIndex].Text.Replace(oldReference, newReference, StringComparison.Ordinal),
        };
        result = string.Concat(lines.Select(line => line.Text + line.NewLine));
        return true;
    }

    public static string Repository(string baseRepository, string branch)
        => DockerRegistryService.RepositoryForBranch(baseRepository, branch);

    public static string ImageReference(ComposeImageInfo image)
        => $"{RegistryPrefix}{Repository(image.BaseRepository, image.Branch)}:{image.Tag}";

    public static string? BranchFromImage(string? image)
    {
        if (!DockerRegistryService.TryParseImageReference(image, out var reference))
            return null;
        return reference.Branch;
    }

    public static bool IsKnownBranch(string? branch)
        => branch is not null && Branches.Contains(branch, StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, ComposeImageInfo>> GetImagesAsync()
        => Parse(await File.ReadAllTextAsync(_composeFilePath));

    public async Task<string?> GetImageReferenceAsync(string service)
    {
        var images = await GetImagesAsync();
        return images.TryGetValue(service, out var image)
            ? ImageReference(image)
            : null;
    }

    public async Task<string> SetBranchAsync(string service, string branch, string? operationId = null)
    {
        await WriteGate.WaitAsync();
        try
        {
            var previous = await File.ReadAllTextAsync(_composeFilePath);
            if (!TryRewrite(previous, service, branch, out var updated, out var error))
                throw new InvalidOperationException(error);

            if (!string.Equals(previous, updated, StringComparison.Ordinal))
            {
                await BackupAsync(previous, operationId);
                await File.WriteAllTextAsync(_composeFilePath, updated);
                _logger.LogInformation("Сервис {Service} переключён на канал {Branch}", service, branch);
            }

            return previous;
        }
        finally
        {
            WriteGate.Release();
        }
    }

    public async Task RestoreAsync(string previousContent)
    {
        await WriteGate.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(_composeFilePath, previousContent);
            _logger.LogWarning("Compose-файл {ComposeFile} восстановлен после ошибки", _composeFilePath);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private async Task BackupAsync(string content, string? operationId = null)
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            var suffix = string.IsNullOrWhiteSpace(operationId)
                ? DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
                : $"operation-{operationId}";
            var path = Path.Combine(_backupDirectory, $"docker-compose-{suffix}.yml");
            await File.WriteAllTextAsync(path, content);
            foreach (var stale in Directory.GetFiles(_backupDirectory, "docker-compose-*.yml")
                         .OrderByDescending(path => path, StringComparer.Ordinal)
                         .Skip(BackupsToKeep))
                File.Delete(stale);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить резервную копию Compose-файла");
            throw new IOException($"Не удалось сохранить резервную копию Compose-файла в {_backupDirectory}", ex);
        }
    }

    private static string BranchFromSuffix(string suffix) => suffix switch
    {
        "-nightly" => "nightly",
        "-dev" => "dev",
        _ => "master",
    };

    private sealed record Line(string Text, string NewLine);

    private static List<Line> SplitLines(string content)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] is not ('\n' or '\r'))
                continue;

            var newLine = content[i] == '\r' && i + 1 < content.Length && content[i + 1] == '\n'
                ? "\r\n"
                : content[i].ToString();
            lines.Add(new Line(content[start..i], newLine));
            i += newLine.Length - 1;
            start = i + 1;
        }

        if (start < content.Length)
            lines.Add(new Line(content[start..], string.Empty));
        return lines;
    }
}
