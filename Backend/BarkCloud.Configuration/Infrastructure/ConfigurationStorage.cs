using BarkCloud.Configuration.Catalog;
using BarkCloud.Configuration.Domain;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Identity;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BarkCloud.Configuration.Infrastructure;

public class ConfigurationStorage : IConfigurationStorage
{
    private readonly ConfigurationContext _context;
    private readonly MetricsCollector _metrics;

    public ConfigurationStorage(ConfigurationContext context, MetricsCollector metrics)
    {
        _context = context;
        _metrics = metrics;
    }

    public async Task<List<ConfigurationItem>> GetConfiguration(ServiceId serviceId)
    {
        if (!SettingsScopes.TryGet(serviceId, out _))
            throw new ArgumentOutOfRangeException(nameof(serviceId), serviceId, "Unknown service id.");

        var selected = new Dictionary<string, ConfigurationItem>(StringComparer.Ordinal);
        foreach (var scope in serviceId == ServiceId.Unknown
                     ? new[] { SettingsScopes.Get(ServiceId.Unknown) }
                     : new[] { SettingsScopes.Get(ServiceId.Unknown), SettingsScopes.Get(serviceId) })
        {
            foreach (var setting in await ReadScopeAsync(scope))
                selected[setting.Key.Length == 0 ? setting.Section : $"{setting.Section}:{setting.Key}"] = setting;
        }

        var reservedNames = await _context.ReservedNames.AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => item.Name)
            .ToListAsync();
        selected["ReservedNames:Usernames"] = new ConfigurationItem
        {
            Section = "ReservedNames",
            Key = "Usernames",
            Value = string.Join(',', reservedNames),
            ServiceId = ServiceId.Unknown,
            EditedAt = DateTime.UtcNow,
            EditedBy = "system",
            EditedFrom = "projection"
        };

        if (serviceId == ServiceId.Files)
        {
            await AddLegacyStorageProjectionAsync(
                selected,
                StorageProfileRoles.UserAvatarsOld,
                "user-avatars");
            await AddLegacyStorageProjectionAsync(
                selected,
                StorageProfileRoles.CloudFilesOld,
                "cloud-files");
        }

        return selected.Values
            .OrderBy(item => item.Section, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<List<ConfigurationItem>> GetAllAsync()
    {
        var result = new List<ConfigurationItem>();
        foreach (var scope in SettingsScopes.All)
            result.AddRange(await ReadScopeAsync(scope));
        result.Add(new ConfigurationItem
        {
            Section = "Features",
            Key = "EmailEnabled",
            Value = await IsEmailConfiguredAsync() ? "true" : "false",
            ServiceId = ServiceId.Unknown,
            EditedAt = DateTime.UtcNow,
            EditedBy = "system",
            EditedFrom = "computed"
        });
        return result;
    }

    public async Task<bool> IsEmailConfiguredAsync()
    {
        var scope = SettingsScopes.Get(ServiceId.Notification);
        var rows = await _context.Settings(scope).AsNoTracking()
            .Where(row => row.Key.StartsWith("Email:"))
            .ToDictionaryAsync(row => row.Key, row => row.Value);

        string[] required = ["Email:Host", "Email:Port", "Email:SenderEmail", "Email:SenderPassword"];
        return required.All(key => rows.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value));
    }

    public async Task SeedMissingCatalogRowsAsync(
        Func<SettingsCatalogEntry, string?> valueFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        var now = DateTime.UtcNow;

        foreach (var scope in SettingsScopes.All)
        {
            var settings = _context.Settings(scope);
            var existing = await settings.ToDictionaryAsync(row => row.Key, StringComparer.Ordinal, cancellationToken);
            foreach (var entry in SettingsCatalog.All.Where(item => item.ServiceId == scope.ServiceId && !item.IsComputed))
            {
                if (!existing.TryGetValue(entry.StorageKey, out var row))
                {
                    settings.Add(new SettingRow
                    {
                        Key = entry.StorageKey,
                        Value = valueFactory(entry) ?? string.Empty,
                        EditedAt = now,
                        EditedBy = "system"
                    });
                    continue;
                }

                if (string.IsNullOrEmpty(row.Value))
                {
                    var value = valueFactory(entry);
                    if (!string.IsNullOrEmpty(value))
                    {
                        row.Value = value;
                        row.EditedAt = now;
                        row.EditedBy = "system";
                    }
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        _metrics.Set("configurations_total", await CountSettingsAsync(cancellationToken));
    }

    public async Task UpdateConfigurationAsync(
        string section,
        string key,
        string value,
        ServiceId serviceId,
        string editedBy,
        string editedFrom)
    {
        var entry = SettingsCatalog.Resolve(serviceId, section, key);
        if (entry.IsEnvironmentManaged)
            throw new InvalidOperationException($"Setting [{serviceId}] {entry.StorageKey} is managed by the environment.");
        value = SettingsValueValidator.ValidateAndNormalize(entry, value);

        var scope = SettingsScopes.Get(serviceId);
        await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await BeginTransactionAsync();
            var row = await GetLockedRowAsync(scope, entry.StorageKey)
                ?? throw new InvalidOperationException($"Catalog row {scope.TableName}.{entry.StorageKey} is missing.");
            var changedAt = DateTime.UtcNow;
            var actor = NormalizeActor(editedBy);
            var previous = row.Value;
            row.Value = value;
            row.EditedAt = changedAt;
            row.EditedBy = actor;
            _context.SettingsHistory.Add(new SettingRevision
            {
                SettingsTable = scope.TableName,
                Key = entry.StorageKey,
                PreviousValue = previous,
                NewValue = row.Value,
                ChangedAt = changedAt,
                ChangedBy = actor,
                ChangedFrom = editedFrom ?? string.Empty,
                ChangeKind = "Update"
            });
            await _context.SaveChangesAsync();
            if (transaction is not null)
                await transaction.CommitAsync();
        });

        _metrics.Increment("configurations_db_writes");
    }

    public async Task<IReadOnlyList<SettingRevision>> GetHistoryAsync(
        string section,
        string key,
        ServiceId serviceId,
        int count,
        CancellationToken cancellationToken = default)
    {
        var entry = SettingsCatalog.Resolve(serviceId, section, key);
        var scope = SettingsScopes.Get(serviceId);
        return await _context.SettingsHistory.AsNoTracking()
            .Where(revision => revision.SettingsTable == scope.TableName && revision.Key == entry.StorageKey)
            .OrderByDescending(revision => revision.ChangedAt)
            .ThenByDescending(revision => revision.Id)
            .Take(Math.Clamp(count, 1, 100))
            .ToListAsync(cancellationToken);
    }

    public async Task RollbackAsync(
        long revisionId,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default)
    {
        var source = await _context.SettingsHistory.AsNoTracking()
            .SingleOrDefaultAsync(revision => revision.Id == revisionId, cancellationToken)
            ?? throw new InvalidOperationException($"Revision {revisionId} was not found.");
        if (!SettingsScopes.TryGet(source.SettingsTable, out var scope))
            throw new InvalidOperationException($"Revision {revisionId} references unknown table {source.SettingsTable}.");
        var entry = SettingsCatalog.Resolve(scope.ServiceId, source.Key);
        if (entry.IsEnvironmentManaged)
            throw new InvalidOperationException($"Setting [{scope.ServiceId}] {entry.StorageKey} is managed by the environment.");

        await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            var row = await GetLockedRowAsync(scope, source.Key, cancellationToken)
                ?? throw new InvalidOperationException($"Settings row {scope.TableName}.{source.Key} was not found.");
            var changedAt = DateTime.UtcNow;
            var actor = NormalizeActor(editedBy);
            var previous = row.Value;
            row.Value = source.PreviousValue;
            row.EditedAt = changedAt;
            row.EditedBy = actor;
            _context.SettingsHistory.Add(new SettingRevision
            {
                SettingsTable = scope.TableName,
                Key = source.Key,
                PreviousValue = previous,
                NewValue = row.Value,
                ChangedAt = changedAt,
                ChangedBy = actor,
                ChangedFrom = editedFrom ?? string.Empty,
                ChangeKind = "Rollback",
                SourceRevisionId = source.Id
            });
            await _context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        });

        _metrics.Increment("configurations_db_writes");
    }

    public async Task<List<string>> GetReservedNamesAsync()
    {
        var names = await _context.ReservedNames.AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => item.Name)
            .ToListAsync();
        _metrics.Set("reserved_names_count", names.Count);
        return names;
    }

    public async Task AddReservedNameAsync(string name)
    {
        var normalized = NormalizeName(name);
        if (await _context.ReservedNames.AnyAsync(item => item.Name == normalized))
            throw new InvalidOperationException($"Имя '{normalized}' уже зарезервировано");
        _context.ReservedNames.Add(new ReservedName { Name = normalized });
        await _context.SaveChangesAsync();
        await UpdateReservedNamesGaugeAsync();
    }

    public async Task UpdateReservedNameAsync(string oldName, string newName)
    {
        var oldNormalized = NormalizeName(oldName);
        var newNormalized = NormalizeName(newName);
        var existing = await _context.ReservedNames.SingleOrDefaultAsync(item => item.Name == oldNormalized)
            ?? throw new InvalidOperationException($"Имя '{oldNormalized}' не найдено");
        if (oldNormalized != newNormalized && await _context.ReservedNames.AnyAsync(item => item.Name == newNormalized))
            throw new InvalidOperationException($"Имя '{newNormalized}' уже зарезервировано");
        _context.ReservedNames.Remove(existing);
        _context.ReservedNames.Add(new ReservedName { Name = newNormalized });
        await _context.SaveChangesAsync();
        await UpdateReservedNamesGaugeAsync();
    }

    public async Task DeleteReservedNameAsync(string name)
    {
        var normalized = NormalizeName(name);
        var existing = await _context.ReservedNames.SingleOrDefaultAsync(item => item.Name == normalized)
            ?? throw new InvalidOperationException($"Имя '{normalized}' не найдено");
        _context.ReservedNames.Remove(existing);
        await _context.SaveChangesAsync();
        await UpdateReservedNamesGaugeAsync();
    }

    private async Task<List<ConfigurationItem>> ReadScopeAsync(SettingsScope scope)
    {
        var rows = await _context.Settings(scope).AsNoTracking().ToListAsync();
        var latestSources = await _context.SettingsHistory.AsNoTracking()
            .Where(revision => revision.SettingsTable == scope.TableName)
            .GroupBy(revision => revision.Key)
            .Select(group => group
                .OrderByDescending(revision => revision.ChangedAt)
                .ThenByDescending(revision => revision.Id)
                .Select(revision => new { revision.Key, revision.ChangedFrom })
                .First())
            .ToListAsync();
        var sources = latestSources.ToDictionary(item => item.Key, item => item.ChangedFrom, StringComparer.Ordinal);

        return rows.Select(row =>
        {
            var entry = SettingsCatalog.Resolve(scope.ServiceId, row.Key);
            return new ConfigurationItem
            {
                Section = entry.Section,
                Key = entry.Key,
                Value = row.Value,
                ServiceId = scope.ServiceId,
                EditedAt = DateTime.SpecifyKind(row.EditedAt, DateTimeKind.Utc),
                EditedBy = row.EditedBy,
                EditedFrom = sources.GetValueOrDefault(row.Key, string.Empty)
            };
        }).ToList();
    }

    private async Task AddLegacyStorageProjectionAsync(
        IDictionary<string, ConfigurationItem> selected,
        string role,
        string legacyBucketId)
    {
        var profile = await _context.StorageProfiles.AsNoTracking()
            .Where(item => item.Role == role)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync();
        if (profile is null)
            return;

        var section = $"S3Buckets:{legacyBucketId}";
        foreach (var (key, value) in new[]
                 {
                     ("ServiceUrl", profile.ServiceUrl),
                     ("AccessKey", profile.AccessKey),
                     ("SecretKey", profile.SecretKey),
                     ("BucketName", profile.BucketName),
                     ("ForcePathStyle", "true")
                 })
        {
            selected[$"{section}:{key}"] = new ConfigurationItem
            {
                Section = section,
                Key = key,
                Value = value,
                ServiceId = ServiceId.Files,
                EditedAt = profile.EditedAt,
                EditedBy = profile.EditedBy,
                EditedFrom = profile.EditedFrom
            };
        }
    }

    private async Task<SettingRow?> GetLockedRowAsync(
        SettingsScope scope,
        string key,
        CancellationToken cancellationToken = default)
    {
        var settings = _context.Settings(scope);
        if (!string.Equals(_context.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return await settings.SingleOrDefaultAsync(row => row.Key == key, cancellationToken);

#pragma warning disable EF1002
        return await settings
            .FromSqlRaw($"SELECT * FROM \"{scope.TableName}\" WHERE \"Key\" = {{0}} FOR UPDATE", key)
            .SingleOrDefaultAsync(cancellationToken);
#pragma warning restore EF1002
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync(cancellationToken)
            : null;

    private async Task<int> CountSettingsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var scope in SettingsScopes.All)
            count += await _context.Settings(scope).CountAsync(cancellationToken);
        return count;
    }

    private async Task UpdateReservedNamesGaugeAsync() =>
        _metrics.Set("reserved_names_count", await _context.ReservedNames.CountAsync());

    private static string NormalizeActor(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim();

    private static string NormalizeName(string? name)
    {
        var normalized = name?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException("Имя не может быть пустым", nameof(name));
        return normalized;
    }
}
