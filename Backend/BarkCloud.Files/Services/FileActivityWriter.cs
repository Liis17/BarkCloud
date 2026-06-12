using System.Text.Json;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;

namespace BarkCloud.Files.Services;

public class FileActivityWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static FileActivityWriter Noop { get; } = new();

    private readonly IFileActivityStorage? _storage;
    private readonly ILogger<FileActivityWriter>? _logger;

    private FileActivityWriter()
    {
    }

    public FileActivityWriter(IFileActivityStorage storage, ILogger<FileActivityWriter> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public Task AddAsync(
        long ownerId,
        Guid fileId,
        long actorUserId,
        string kind,
        string summary,
        Guid? entryId = null,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        return AddManyAsync(
            new[]
            {
                Create(ownerId, fileId, actorUserId, kind, summary, entryId, details)
            },
            cancellationToken);
    }

    public async Task AddManyAsync(IEnumerable<FileActivityEvent> activities, CancellationToken cancellationToken = default)
    {
        var list = activities.ToList();
        if (list.Count == 0)
            return;
        if (_storage is null)
            return;

        try
        {
            await _storage.AddRange(list, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Не удалось записать историю активности файлов ({Count} событий)", list.Count);
        }
    }

    public static FileActivityEvent Create(
        long ownerId,
        Guid fileId,
        long actorUserId,
        string kind,
        string summary,
        Guid? entryId = null,
        object? details = null)
    {
        return new FileActivityEvent
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            FileId = fileId,
            EntryId = entryId,
            ActorUserId = actorUserId,
            Kind = kind,
            Summary = summary,
            DetailsJson = details is null ? "{}" : JsonSerializer.Serialize(details, JsonOptions),
            CreatedAt = DateTime.UtcNow
        };
    }
}
