using BarkCloud.Proto.Configuration;

using Grpc.Core;

namespace BarkCloud.Web.Infrastructure;

public sealed record ServerSettingDto(
    int ServiceId,
    string Section,
    string Key,
    string Value,
    bool IsSensitive,
    bool HasValue,
    bool IsReadOnly,
    string ValueKind,
    string[] RestartTargets,
    DateTimeOffset? EditedAt,
    string EditedBy,
    string EditedFrom);

public sealed record ServerSettingRevisionDto(
    long Id,
    string? PreviousValue,
    string? NewValue,
    bool IsSensitive,
    bool PreviousHasValue,
    bool NewHasValue,
    DateTimeOffset? ChangedAt,
    string ChangedBy,
    string ChangedFrom,
    string ChangeKind,
    long? SourceRevisionId);

public sealed record ServerStorageProfileDto(
    string ProfileId,
    string Role,
    int Version,
    string ServiceUrl,
    bool HasAccessKey,
    bool HasSecretKey,
    string BucketName,
    bool IsR2,
    bool IsActive,
    bool IsLegacy,
    DateTimeOffset? EditedAt,
    string EditedBy,
    string EditedFrom);

public sealed record ServerStorageRevisionDto(
    long Id,
    string ProfileId,
    DateTimeOffset? ChangedAt,
    string ChangedBy,
    string ChangedFrom,
    string ChangeKind,
    long? SourceRevisionId);

public sealed record ServerSettingsDto(
    IReadOnlyList<ServerSettingDto> Settings,
    IReadOnlyList<string> ReservedNames,
    IReadOnlyList<ServerStorageProfileDto> StorageProfiles,
    IReadOnlyList<ServerStorageRevisionDto> StorageRevisions);

public sealed record ConfigurationMutationResult(
    bool Success,
    string Message,
    IReadOnlyList<string> RestartTargets);

public sealed record StorageProfileEdit(
    string Role,
    string ServiceUrl,
    string AccessKey,
    string SecretKey,
    string BucketName,
    bool IsR2,
    bool IsLegacy,
    string? ProfileId,
    bool ConfirmLegacyMutation);

public sealed class ConfigurationManagementGateway(
    ConfigurationApi.ConfigurationApiClient configuration,
    IConfiguration hostConfiguration,
    S3AccessChecker s3AccessChecker)
{
    private readonly Metadata? _headers = BuildHeaders(hostConfiguration);

    public async Task<ServerSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var allTask = configuration.GetAllConfigurationsAsync(
            new GetAllConfigurationsRequest(), _headers, cancellationToken: cancellationToken).ResponseAsync;
        var reservedTask = configuration.GetReservedNamesAsync(
            new GetReservedNamesRequest(), _headers, cancellationToken: cancellationToken).ResponseAsync;
        var profilesTask = configuration.GetStorageProfilesAsync(
            new GetStorageProfilesRequest { IncludeRevisions = true }, _headers, cancellationToken: cancellationToken).ResponseAsync;
        await Task.WhenAll(allTask, reservedTask, profilesTask);

        return new ServerSettingsDto(
            allTask.Result.Configurations
                .Where(configuration => !IsTokenSetting(configuration))
                .Select(Mask)
                .ToArray(),
            reservedTask.Result.Names.ToArray(),
            profilesTask.Result.Profiles.Select(Mask).ToArray(),
            profilesTask.Result.Revisions.Select(revision => new ServerStorageRevisionDto(
                revision.Id,
                revision.ProfileId,
                revision.ChangedAt?.ToDateTimeOffset(),
                revision.ChangedBy,
                revision.ChangedFrom,
                revision.ChangeKind,
                revision.HasSourceRevision ? revision.SourceRevisionId : null)).ToArray());
    }

    public async Task<ConfigurationMutationResult> SaveValueAsync(
        int serviceId,
        string section,
        string key,
        string? value,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var current = await configuration.GetAllConfigurationsAsync(
            new GetAllConfigurationsRequest(), _headers, cancellationToken: cancellationToken);
        var item = current.Configurations.SingleOrDefault(candidate =>
            candidate.ServiceId == serviceId && candidate.Section == section && candidate.Key == key);
        if (item is null)
            return new ConfigurationMutationResult(false, "Неизвестная настройка", []);
        if (item.IsReadOnly)
            return new ConfigurationMutationResult(false, "Настройка управляется через .env/compose и доступна только для чтения", []);
        if (item.IsSensitive && string.IsNullOrEmpty(value))
            return new ConfigurationMutationResult(false, "Введите новое секретное значение", []);

        var result = await configuration.UpdateConfigurationAsync(new UpdateConfigurationRequest
        {
            ServiceId = serviceId,
            Section = section,
            Key = key,
            Value = value ?? string.Empty,
            EditedBy = actor,
            EditedFrom = "web-settings"
        }, _headers, cancellationToken: cancellationToken);
        return new ConfigurationMutationResult(result.Success, result.Message, item.RestartTargets.ToArray());
    }

    public async Task<IReadOnlyList<ServerSettingRevisionDto>> GetHistoryAsync(
        int serviceId,
        string section,
        string key,
        int count,
        CancellationToken cancellationToken = default)
    {
        var response = await configuration.GetConfigurationHistoryAsync(new GetConfigurationHistoryRequest
        {
            ServiceId = serviceId,
            Section = section,
            Key = key,
            Count = count
        }, _headers, cancellationToken: cancellationToken);
        return response.Revisions.Select(revision => new ServerSettingRevisionDto(
            revision.Id,
            revision.IsSensitive ? null : revision.PreviousValue,
            revision.IsSensitive ? null : revision.NewValue,
            revision.IsSensitive,
            revision.PreviousHasValue,
            revision.NewHasValue,
            revision.ChangedAt?.ToDateTimeOffset(),
            revision.ChangedBy,
            revision.ChangedFrom,
            revision.ChangeKind,
            revision.HasSourceRevision ? revision.SourceRevisionId : null)).ToArray();
    }

    public async Task<ConfigurationMutationResult> RollbackAsync(
        long revisionId,
        int serviceId,
        string section,
        string key,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await configuration.GetAllConfigurationsAsync(
            new GetAllConfigurationsRequest(), _headers, cancellationToken: cancellationToken);
        var item = snapshot.Configurations.SingleOrDefault(candidate =>
            candidate.ServiceId == serviceId && candidate.Section == section && candidate.Key == key);
        if (item is null)
            return new ConfigurationMutationResult(false, "Неизвестная настройка", []);
        var response = await configuration.RollbackConfigurationAsync(new RollbackConfigurationRequest
        {
            RevisionId = revisionId,
            EditedBy = actor,
            EditedFrom = "web-settings"
        }, _headers, cancellationToken: cancellationToken);
        return new ConfigurationMutationResult(response.Success, response.Message, item.RestartTargets.ToArray());
    }

    public async Task<ConfigurationMutationResult> SaveStorageProfileAsync(
        StorageProfileEdit edit,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var profiles = await configuration.GetStorageProfilesAsync(
            new GetStorageProfilesRequest(), _headers, cancellationToken: cancellationToken);
        var current = FindStorageProfile(profiles.Profiles, edit);
        var accessKey = string.IsNullOrEmpty(edit.AccessKey) ? current?.AccessKey ?? string.Empty : edit.AccessKey;
        var response = await configuration.SaveStorageProfileAsync(new SaveStorageProfileRequest
        {
            Role = edit.Role,
            ServiceUrl = edit.ServiceUrl,
            AccessKey = accessKey,
            SecretKey = edit.SecretKey,
            BucketName = edit.BucketName,
            IsR2 = edit.IsR2,
            IsLegacy = edit.IsLegacy,
            ProfileId = edit.ProfileId ?? string.Empty,
            ConfirmLegacyMutation = edit.ConfirmLegacyMutation,
            EditedBy = actor,
            EditedFrom = "web-settings"
        }, _headers, cancellationToken: cancellationToken);
        return new ConfigurationMutationResult(response.Success, response.Message, ["files"]);
    }

    public async Task<StorageAccessCheckResult> CheckStorageProfileAccessAsync(
        StorageProfileEdit edit,
        CancellationToken cancellationToken = default)
    {
        var profiles = await configuration.GetStorageProfilesAsync(
            new GetStorageProfilesRequest(), _headers, cancellationToken: cancellationToken);
        var current = FindStorageProfile(profiles.Profiles, edit);

        if (!string.IsNullOrWhiteSpace(edit.ProfileId) && current is null)
            return new StorageAccessCheckResult(false, "S3-профиль не найден");
        if (current is not null && !string.Equals(current.Role, edit.Role, StringComparison.Ordinal))
            return new StorageAccessCheckResult(false, "S3-профиль не соответствует роли");

        return await s3AccessChecker.CheckAsync(new S3AccessCheckRequest(
            edit.ServiceUrl,
            string.IsNullOrEmpty(edit.AccessKey) ? current?.AccessKey ?? string.Empty : edit.AccessKey,
            string.IsNullOrEmpty(edit.SecretKey) ? current?.SecretKey ?? string.Empty : edit.SecretKey,
            edit.BucketName,
            edit.IsR2), cancellationToken);
    }

    public async Task<ConfigurationMutationResult> ActivateStorageProfileAsync(
        string profileId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var response = await configuration.ActivateStorageProfileAsync(new ActivateStorageProfileRequest
        {
            ProfileId = profileId,
            EditedBy = actor,
            EditedFrom = "web-settings"
        }, _headers, cancellationToken: cancellationToken);
        return new ConfigurationMutationResult(response.Success, response.Message, ["files"]);
    }

    public async Task<ConfigurationMutationResult> DisableStorageRoleAsync(
        string role,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var response = await configuration.DisableStorageRoleAsync(new DisableStorageRoleRequest
        {
            Role = role,
            EditedBy = actor,
            EditedFrom = "web-settings"
        }, _headers, cancellationToken: cancellationToken);
        return new ConfigurationMutationResult(response.Success, response.Message, ["files"]);
    }

    public async Task<ConfigurationMutationResult> AddReservedNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var response = await configuration.AddReservedNameAsync(
            new AddReservedNameRequest { Name = name }, _headers, cancellationToken: cancellationToken);
        return new ConfigurationMutationResult(response.Success, response.Message, ["users"]);
    }

    public async Task<ConfigurationMutationResult> UpdateReservedNameAsync(
        string oldName,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var response = await configuration.UpdateReservedNameAsync(
            new UpdateReservedNameRequest { OldName = oldName, NewName = newName }, _headers, cancellationToken: cancellationToken);
        return new ConfigurationMutationResult(response.Success, response.Message, ["users"]);
    }

    public async Task<ConfigurationMutationResult> DeleteReservedNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var response = await configuration.DeleteReservedNameAsync(
            new DeleteReservedNameRequest { Name = name }, _headers, cancellationToken: cancellationToken);
        return new ConfigurationMutationResult(response.Success, response.Message, ["users"]);
    }

    public static ServerSettingDto Mask(ConfigurationItem item) => new(
        item.ServiceId,
        item.Section,
        item.Key,
        item.IsSensitive ? string.Empty : item.Value,
        item.IsSensitive,
        item.HasValue,
        item.IsReadOnly,
        item.ValueKind,
        item.RestartTargets.ToArray(),
        item.EditedAt?.ToDateTimeOffset(),
        item.EditedBy,
        item.EditedFrom);

    public static ServerStorageProfileDto Mask(StorageProfileItem profile) => new(
        profile.ProfileId,
        profile.Role,
        profile.Version,
        profile.ServiceUrl,
        !string.IsNullOrEmpty(profile.AccessKey),
        profile.HasSecretKey,
        profile.BucketName,
        profile.IsR2,
        profile.IsActive,
        profile.IsLegacy,
        profile.EditedAt?.ToDateTimeOffset(),
        profile.EditedBy,
        profile.EditedFrom);

    private static StorageProfileItem? FindStorageProfile(
        IEnumerable<StorageProfileItem> profiles,
        StorageProfileEdit edit) => !string.IsNullOrWhiteSpace(edit.ProfileId)
            ? profiles.SingleOrDefault(profile => profile.ProfileId == edit.ProfileId)
            : profiles.Where(profile => profile.Role == edit.Role)
                .OrderByDescending(profile => profile.IsActive)
                .ThenByDescending(profile => profile.Version)
                .FirstOrDefault();

    private static bool IsTokenSetting(ConfigurationItem item) =>
        string.Equals(item.Key, "Token", StringComparison.OrdinalIgnoreCase);

    private static Metadata? BuildHeaders(IConfiguration configuration)
    {
        var accessKey = configuration["CONFIGURATION_ACCESS_KEY"]
                        ?? Environment.GetEnvironmentVariable("CONFIGURATION_ACCESS_KEY");
        return string.IsNullOrEmpty(accessKey)
            ? null
            : new Metadata { { "x-config-access-key", accessKey } };
    }
}
