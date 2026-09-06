using Microsoft.Extensions.Caching.Memory;

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// Читает версии BarkCloud-образов из Docker Registry. Registry используется только
/// для чтения: авторизация для pull остаётся обязанностью Docker/Compose.
/// </summary>
public sealed class DockerRegistryService
{
    public const string RegistryHost = "docker.barkfluff.com";
    private const string RegistryPrefix = RegistryHost + "/";
    private static readonly SemaphoreSlim ManifestRequestGate = new(8, 8);
    private static readonly Regex SemverTagRegex = new(
        "^(?<major>0|[1-9]\\d*)\\.(?<minor>0|[1-9]\\d*)\\.(?<patch>0|[1-9]\\d*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DockerRegistryService> _logger;

    public DockerRegistryService(HttpClient httpClient, IMemoryCache cache, ILogger<DockerRegistryService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ImageVersionStatus> GetVersionStatusAsync(
        string? image,
        string? imageDigest = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseImageReference(image, out var reference))
        {
            return new ImageVersionStatus
            {
                State = ImageVersionState.Unknown,
                Error = "Образ не относится к реестру BarkCloud",
            };
        }

        try
        {
            var tags = await GetTagsAsync(reference.Repository, cancellationToken);
            var versions = tags
                .Select(tag => (Tag: tag, Parsed: TryParseSemver(tag, out var version) ? version : null))
                .Where(item => item.Parsed is not null)
                .Select(item => (item.Tag, Version: item.Parsed!))
                .ToList();

            if (versions.Count == 0)
            {
                return new ImageVersionStatus
                {
                    Repository = reference.Repository,
                    Tag = reference.Tag,
                    Branch = reference.Branch,
                    State = ImageVersionState.Unknown,
                    Error = "В реестре нет SemVer-тегов",
                };
            }

            var latest = versions.MaxBy(item => item.Version);
            string? currentVersion = TryParseSemver(reference.Tag, out var taggedVersion)
                ? reference.Tag
                : null;
            Version? currentParsedVersion = TryParseSemver(reference.Tag, out _)
                ? taggedVersion
                : null;

            var installedDigest = !string.IsNullOrWhiteSpace(imageDigest)
                ? imageDigest
                : reference.Digest;
            if (currentParsedVersion is null && !string.IsNullOrWhiteSpace(installedDigest))
            {
                var normalizedDigest = NormalizeDigest(installedDigest);
                var manifestDigests = await Task.WhenAll(versions.Select(async item => new
                {
                    item.Tag,
                    item.Version,
                    Digest = await GetManifestDigestAsync(reference.Repository, item.Tag, cancellationToken),
                }));

                var installed = manifestDigests.FirstOrDefault(item =>
                    item.Digest is not null &&
                    string.Equals(item.Digest, normalizedDigest, StringComparison.OrdinalIgnoreCase));

                if (installed is not null)
                {
                    currentVersion = installed.Tag;
                    currentParsedVersion = installed.Version;
                }
            }

            return new ImageVersionStatus
            {
                Repository = reference.Repository,
                Tag = reference.Tag,
                Branch = reference.Branch,
                CurrentVersion = currentVersion,
                LatestVersion = latest.Tag,
                UpdateAvailable = currentParsedVersion is null
                    ? null
                    : currentParsedVersion.CompareTo(latest.Version) < 0,
                State = currentParsedVersion is null ? ImageVersionState.Unknown : ImageVersionState.Ready,
                Error = currentParsedVersion is null ? "Текущий образ не сопоставлен с SemVer" : null,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RegistryUnavailable(reference, "Проверка реестра превысила время ожидания");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Не удалось проверить версии образа {Repository}", reference.Repository);
            return RegistryUnavailable(reference, $"Реестр недоступен: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Реестр вернул некорректный ответ для {Repository}", reference.Repository);
            return RegistryUnavailable(reference, "Реестр вернул некорректный ответ");
        }
    }

    public async Task<bool> RepositoryExistsAsync(string repository, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"barkcloud-registry:exists:{repository}";
        if (_cache.TryGetValue(cacheKey, out bool cached))
            return cached;

        try
        {
            using var response = await _httpClient.GetAsync($"/v2/{repository}/tags/list", cancellationToken);
            var exists = response.IsSuccessStatusCode;
            _cache.Set(cacheKey, exists, exists ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1));
            return exists;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Не удалось проверить репозиторий {Repository}", repository);
            return false;
        }
    }

    public static bool TryParseImageReference(string? image, out ImageReference reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(image) || !image.StartsWith(RegistryPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = image[RegistryPrefix.Length..];
        var digestSeparator = value.LastIndexOf('@');
        var repositoryAndTag = digestSeparator > value.LastIndexOf('/')
            ? value[..digestSeparator]
            : value;
        var digest = digestSeparator > value.LastIndexOf('/') && digestSeparator < value.Length - 1
            ? value[(digestSeparator + 1)..]
            : null;
        if (digestSeparator > value.LastIndexOf('/') && string.IsNullOrWhiteSpace(digest))
            return false;

        var separator = repositoryAndTag.LastIndexOf(':');
        var repository = separator > repositoryAndTag.LastIndexOf('/')
            ? repositoryAndTag[..separator]
            : repositoryAndTag;
        var tag = separator > repositoryAndTag.LastIndexOf('/')
            ? repositoryAndTag[(separator + 1)..]
            : null;
        if (string.IsNullOrWhiteSpace(repository) || tag is not null && tag.Length == 0)
            return false;

        if (!repository.StartsWith("barkcloud-", StringComparison.OrdinalIgnoreCase))
            return false;

        var branch = "master";
        var baseRepository = repository;
        if (repository.EndsWith("-nightly", StringComparison.OrdinalIgnoreCase))
        {
            branch = "nightly";
            baseRepository = repository[..^"-nightly".Length];
        }
        else if (repository.EndsWith("-dev", StringComparison.OrdinalIgnoreCase))
        {
            branch = "dev";
            baseRepository = repository[..^"-dev".Length];
        }

        if (baseRepository.Length <= "barkcloud-".Length)
            return false;

        reference = new ImageReference(repository, baseRepository, branch, tag, digest);
        return true;
    }

    /// <summary>
    /// Возвращает ссылку на образ для проверки реестра. Docker может вернуть в поле
    /// <c>Image</c> только короткий ID, если контейнер был создан по digest или без тега;
    /// в этом случае используем каноническую ссылку из Compose.
    /// </summary>
    public static string? ResolveImageReference(string? runtimeImage, string? composeImage)
    {
        if (TryParseImageReference(runtimeImage, out _))
            return runtimeImage;
        if (TryParseImageReference(composeImage, out _))
            return composeImage;
        return runtimeImage ?? composeImage;
    }

    public static string RepositoryForBranch(string baseRepository, string branch) => branch switch
    {
        "nightly" => $"{baseRepository}-nightly",
        "dev" => $"{baseRepository}-dev",
        "master" => baseRepository,
        _ => throw new ArgumentException($"Неизвестный канал: {branch}", nameof(branch)),
    };

    private async Task<string[]> GetTagsAsync(string repository, CancellationToken cancellationToken)
    {
        var cacheKey = $"barkcloud-registry:tags:{repository}";
        if (_cache.TryGetValue(cacheKey, out string[]? cached) && cached is not null)
            return cached;

        using var response = await _httpClient.GetAsync($"/v2/{repository}/tags/list", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var tags = document.RootElement.TryGetProperty("tags", out var tagsElement) &&
                   tagsElement.ValueKind == JsonValueKind.Array
            ? tagsElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Cast<string>()
                .ToArray()
            : [];

        _cache.Set(cacheKey, tags, TimeSpan.FromMinutes(5));
        return tags;
    }

    private async Task<string?> GetManifestDigestAsync(string repository, string tag, CancellationToken cancellationToken)
    {
        var cacheKey = $"barkcloud-registry:manifest:{repository}:{tag}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
            return cached;

        await ManifestRequestGate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(cacheKey, out cached))
                return cached;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v2/{repository}/manifests/{tag}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.index.v1+json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.list.v2+json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v2+json"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} для manifest {repository}:{tag}");

            var digest = response.Headers.TryGetValues("Docker-Content-Digest", out var values)
                ? values.FirstOrDefault()
                : null;
            if (string.IsNullOrWhiteSpace(digest))
                throw new HttpRequestException($"В ответе manifest {repository}:{tag} отсутствует Docker-Content-Digest");

            _cache.Set(cacheKey, digest, TimeSpan.FromMinutes(5));
            return digest;
        }
        finally
        {
            ManifestRequestGate.Release();
        }
    }

    private static bool TryParseSemver(string? tag, out Version version)
    {
        version = new Version();
        if (tag is null)
            return false;

        var match = SemverTagRegex.Match(tag);
        if (!match.Success)
            return false;

        return int.TryParse(match.Groups["major"].Value, out var major) &&
               int.TryParse(match.Groups["minor"].Value, out var minor) &&
               int.TryParse(match.Groups["patch"].Value, out var patch) &&
               (version = new Version(major, minor, patch)) is not null;
    }

    private static string NormalizeDigest(string digest)
    {
        var separator = digest.LastIndexOf('@');
        return separator >= 0 ? digest[(separator + 1)..] : digest;
    }

    private static ImageVersionStatus RegistryUnavailable(ImageReference reference, string error)
        => new()
        {
            Repository = reference.Repository,
            Tag = reference.Tag,
            Branch = reference.Branch,
            State = ImageVersionState.RegistryUnavailable,
            Error = error,
        };
}

public readonly record struct ImageReference(
    string Repository,
    string BaseRepository,
    string Branch,
    string? Tag,
    string? Digest = null);
