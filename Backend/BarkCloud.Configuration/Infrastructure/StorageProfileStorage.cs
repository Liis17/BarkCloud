using BarkCloud.Configuration.Domain;
using BarkCloud.GrpcServer.Metrics;

using Microsoft.EntityFrameworkCore;

using System.Text.Json;

namespace BarkCloud.Configuration.Infrastructure;

public sealed record StorageProfileInput(
    string Role,
    string ServiceUrl,
    string AccessKey,
    string SecretKey,
    string BucketName,
    bool IsR2,
    bool IsLegacy,
    string? ProfileId,
    bool ConfirmLegacyMutation);

public static class StorageProfileRoles
{
    public const string Universal = "universal";
    public const string UserAvatarsOld = "user-avatars-old";
    public const string CloudFilesOld = "cloud-files-old";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Universal,
        "avatars",
        "images",
        "videos",
        "audio",
        "documents",
        "other",
        "previews",
        UserAvatarsOld,
        CloudFilesOld
    };

    public static bool IsCompatibility(string role) =>
        role is UserAvatarsOld or CloudFilesOld;
}

public sealed class StorageProfileStorage
{
    private readonly ConfigurationContext _context;
    private readonly MetricsCollector _metrics;

    public StorageProfileStorage(ConfigurationContext context, MetricsCollector metrics)
    {
        _context = context;
        _metrics = metrics;
    }

    public async Task<IReadOnlyList<StorageProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _context.StorageProfiles.AsNoTracking()
            .OrderBy(profile => profile.Role)
            .ThenByDescending(profile => profile.Version)
            .ToListAsync(cancellationToken);

    public async Task<StorageProfile> SaveAsync(
        StorageProfileInput input,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default)
    {
        ValidateRole(input.Role);
        if (input.IsLegacy != StorageProfileRoles.IsCompatibility(input.Role))
            throw new InvalidOperationException("Legacy flag is allowed only for compatibility storage roles.");
        var actor = NormalizeActor(editedBy);
        var source = editedFrom ?? string.Empty;

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var profiles = await LockedProfilesAsync(cancellationToken);
        var target = ResolveTarget(profiles, input);
        if (target is not null && target.Role != input.Role)
            throw new InvalidOperationException($"Storage profile '{target.ProfileId}' belongs to role '{target.Role}'.");
        var secret = string.IsNullOrEmpty(input.SecretKey) ? target?.SecretKey ?? string.Empty : input.SecretKey;
        ValidateComplete(input.ServiceUrl, input.AccessKey, secret, input.BucketName);
        if (target is not null
            && (target.IsLegacy || StorageProfileRoles.IsCompatibility(target.Role))
            && (LocationChanged(target, input)
                || target.AccessKey != input.AccessKey.Trim()
                || target.SecretKey != secret)
            && !input.ConfirmLegacyMutation)
        {
            throw new InvalidOperationException("Изменение legacy-профиля требует подтверждения.");
        }

        StorageProfile saved;
        if (target is null)
        {
            saved = CreateProfile(input, secret, NextVersion(profiles, input.Role), actor, source);
            _context.StorageProfiles.Add(saved);
            AddRevision(saved.ProfileId, null, saved, "Create", actor, source);
        }
        else if (LocationChanged(target, input))
        {
            if (target.IsLegacy || StorageProfileRoles.IsCompatibility(target.Role))
            {
                if (!input.ConfirmLegacyMutation)
                    throw new InvalidOperationException("Изменение расположения legacy-профиля требует подтверждения.");
                var before = Snapshot(target);
                Apply(target, input, secret, actor, source);
                saved = target;
                AddRevision(target.ProfileId, before, target, "LegacyCorrection", actor, source);
            }
            else
            {
                foreach (var profile in profiles.Where(profile => profile.Role == input.Role && profile.IsActive))
                {
                    var before = Snapshot(profile);
                    profile.IsActive = false;
                    profile.EditedAt = DateTime.UtcNow;
                    profile.EditedBy = actor;
                    profile.EditedFrom = source;
                    AddRevision(profile.ProfileId, before, profile, "Superseded", actor, source);
                }
                saved = CreateProfile(input, secret, NextVersion(profiles, input.Role), actor, source);
                _context.StorageProfiles.Add(saved);
                AddRevision(saved.ProfileId, null, saved, "NewVersion", actor, source);
            }
        }
        else
        {
            saved = target;
            var sameLocation = profiles.Where(profile => SameLocation(profile, target)).ToArray();
            var rotatesCredentials = sameLocation.Any(profile =>
                profile.AccessKey != input.AccessKey.Trim() || profile.SecretKey != secret);
            if (rotatesCredentials
                && sameLocation.Any(profile => profile.IsLegacy || StorageProfileRoles.IsCompatibility(profile.Role))
                && !input.ConfirmLegacyMutation)
            {
                throw new InvalidOperationException(
                    "Ротация затрагивает legacy-профиль той же физической локации и требует подтверждения.");
            }
            foreach (var profile in sameLocation)
            {
                if (profile.AccessKey == input.AccessKey && profile.SecretKey == secret)
                    continue;
                var before = Snapshot(profile);
                profile.AccessKey = input.AccessKey;
                profile.SecretKey = secret;
                profile.EditedAt = DateTime.UtcNow;
                profile.EditedBy = actor;
                profile.EditedFrom = source;
                AddRevision(profile.ProfileId, before, profile, "CredentialsRotation", actor, source);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _metrics.Increment("storage_profile_writes");
        return saved;
    }

    public async Task ActivateAsync(
        string profileId,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(editedBy);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var profiles = await LockedProfilesAsync(cancellationToken);
        var selected = profiles.SingleOrDefault(profile => profile.ProfileId == profileId)
            ?? throw new InvalidOperationException($"Storage profile '{profileId}' was not found.");
        if (selected.IsLegacy || StorageProfileRoles.IsCompatibility(selected.Role))
            throw new InvalidOperationException("Legacy-профиль нельзя использовать для новых объектов.");
        ValidateComplete(selected.ServiceUrl, selected.AccessKey, selected.SecretKey, selected.BucketName);

        foreach (var profile in profiles.Where(profile => profile.Role == selected.Role && profile.IsActive != (profile == selected)))
        {
            var before = Snapshot(profile);
            profile.IsActive = profile == selected;
            profile.EditedAt = DateTime.UtcNow;
            profile.EditedBy = actor;
            profile.EditedFrom = editedFrom ?? string.Empty;
            AddRevision(profile.ProfileId, before, profile, profile.IsActive ? "Activate" : "Deactivate", actor, editedFrom ?? string.Empty);
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DisableRoleAsync(
        string role,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default)
    {
        ValidateRole(role);
        if (role == StorageProfileRoles.Universal)
            throw new InvalidOperationException("Universal storage profile cannot be disabled.");
        if (StorageProfileRoles.IsCompatibility(role))
            throw new InvalidOperationException("Legacy roles are never used for new objects.");

        var actor = NormalizeActor(editedBy);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var profiles = await LockedProfilesAsync(cancellationToken);
        foreach (var profile in profiles.Where(profile => profile.Role == role && profile.IsActive))
        {
            var before = Snapshot(profile);
            profile.IsActive = false;
            profile.EditedAt = DateTime.UtcNow;
            profile.EditedBy = actor;
            profile.EditedFrom = editedFrom ?? string.Empty;
            AddRevision(profile.ProfileId, before, profile, "DisableRole", actor, editedFrom ?? string.Empty);
        }
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StorageProfileRevision>> GetHistoryAsync(
        string profileId,
        int count,
        CancellationToken cancellationToken = default) =>
        await _context.StorageProfileRevisions.AsNoTracking()
            .Where(revision => revision.ProfileId == profileId)
            .OrderByDescending(revision => revision.ChangedAt)
            .ThenByDescending(revision => revision.Id)
            .Take(Math.Clamp(count, 1, 100))
            .ToListAsync(cancellationToken);

    private async Task<List<StorageProfile>> LockedProfilesAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_context.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return await _context.StorageProfiles.ToListAsync(cancellationToken);

        return await _context.StorageProfiles
            .FromSqlRaw("SELECT * FROM \"StorageProfiles\" FOR UPDATE")
            .ToListAsync(cancellationToken);
    }

    private static StorageProfile? ResolveTarget(IReadOnlyList<StorageProfile> profiles, StorageProfileInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.ProfileId))
            return profiles.SingleOrDefault(profile => profile.ProfileId == input.ProfileId)
                ?? throw new InvalidOperationException($"Storage profile '{input.ProfileId}' was not found.");
        return profiles.Where(profile => profile.Role == input.Role)
            .OrderByDescending(profile => profile.IsActive)
            .ThenByDescending(profile => profile.Version)
            .FirstOrDefault();
    }

    private static StorageProfile CreateProfile(
        StorageProfileInput input,
        string secret,
        int version,
        string actor,
        string source)
    {
        var now = DateTime.UtcNow;
        var legacy = StorageProfileRoles.IsCompatibility(input.Role);
        return new StorageProfile
        {
            ProfileId = $"{input.Role}-v{version}",
            Role = input.Role,
            Version = version,
            ServiceUrl = NormalizeUrl(input.ServiceUrl),
            AccessKey = input.AccessKey.Trim(),
            SecretKey = secret,
            BucketName = input.BucketName.Trim(),
            IsR2 = input.IsR2,
            IsLegacy = legacy,
            IsActive = !legacy,
            CreatedAt = now,
            CreatedBy = actor,
            CreatedFrom = source,
            EditedAt = now,
            EditedBy = actor,
            EditedFrom = source
        };
    }

    private static void Apply(
        StorageProfile target,
        StorageProfileInput input,
        string secret,
        string actor,
        string source)
    {
        target.ServiceUrl = NormalizeUrl(input.ServiceUrl);
        target.AccessKey = input.AccessKey.Trim();
        target.SecretKey = secret;
        target.BucketName = input.BucketName.Trim();
        target.IsR2 = input.IsR2;
        target.EditedAt = DateTime.UtcNow;
        target.EditedBy = actor;
        target.EditedFrom = source;
    }

    private void AddRevision(
        string profileId,
        string? before,
        StorageProfile after,
        string kind,
        string actor,
        string source)
    {
        _context.StorageProfileRevisions.Add(new StorageProfileRevision
        {
            ProfileId = profileId,
            PreviousValue = before ?? string.Empty,
            NewValue = Snapshot(after),
            ChangedAt = DateTime.UtcNow,
            ChangedBy = actor,
            ChangedFrom = source,
            ChangeKind = kind
        });
    }

    private static string Snapshot(StorageProfile profile) => JsonSerializer.Serialize(new
    {
        profile.ProfileId,
        profile.Role,
        profile.Version,
        profile.ServiceUrl,
        profile.AccessKey,
        profile.SecretKey,
        profile.BucketName,
        profile.IsR2,
        profile.IsActive,
        profile.IsLegacy
    });

    private static int NextVersion(IEnumerable<StorageProfile> profiles, string role) =>
        profiles.Where(profile => profile.Role == role).Select(profile => profile.Version).DefaultIfEmpty().Max() + 1;

    private static bool LocationChanged(StorageProfile profile, StorageProfileInput input) =>
        !string.Equals(profile.ServiceUrl, NormalizeUrl(input.ServiceUrl), StringComparison.OrdinalIgnoreCase)
        || !string.Equals(profile.BucketName, input.BucketName.Trim(), StringComparison.Ordinal)
        || profile.IsR2 != input.IsR2;

    private static bool SameLocation(StorageProfile left, StorageProfile right) =>
        string.Equals(left.ServiceUrl, right.ServiceUrl, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.BucketName, right.BucketName, StringComparison.Ordinal)
        && left.IsR2 == right.IsR2;

    private static string NormalizeUrl(string value) => value.Trim().TrimEnd('/');

    private static string NormalizeActor(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static void ValidateRole(string role)
    {
        if (!StorageProfileRoles.All.Contains(role))
            throw new InvalidOperationException($"Unknown storage role '{role}'.");
    }

    private static void ValidateComplete(string serviceUrl, string accessKey, string secretKey, string bucketName)
    {
        var values = new[] { serviceUrl, accessKey, secretKey, bucketName };
        if (values.All(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Storage profile is empty. Use the separate disable action.");
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Storage profile requires URL, bucket name, access key and secret key.");
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Storage profile URL must be an absolute HTTP(S) URL.");
    }
}
