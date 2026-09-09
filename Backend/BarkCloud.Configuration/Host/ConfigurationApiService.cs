using BarkCloud.Configuration.Features.AddReservedName;
using BarkCloud.Configuration.Features.DeleteReservedName;
using BarkCloud.Configuration.Features.GetConfiguration;
using BarkCloud.Configuration.Features.GetReservedNames;
using BarkCloud.Configuration.Features.UpdateConfiguration;
using BarkCloud.Configuration.Features.UpdateReservedName;
using BarkCloud.Configuration.Catalog;
using BarkCloud.Configuration.Domain;
using BarkCloud.Configuration.Infrastructure;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Proto.Configuration;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using System.Diagnostics;

using DomainStorageProfile = BarkCloud.Configuration.Domain.StorageProfile;

namespace BarkCloud.Configuration.Host;

public class ConfigurationApiService : BarkCloud.Proto.Configuration.ConfigurationApi.ConfigurationApiBase
{
    private readonly IMediator _mediator;
    private readonly MetricsCollector _metrics;
    private readonly IConfigurationStorage _configurationStorage;
    private readonly StorageProfileStorage _storageProfiles;

    public ConfigurationApiService(
        IMediator mediator,
        MetricsCollector metrics,
        IConfigurationStorage configurationStorage,
        StorageProfileStorage storageProfiles)
    {
        _mediator = mediator;
        _metrics = metrics;
        _configurationStorage = configurationStorage;
        _storageProfiles = storageProfiles;
    }

    public override async Task<GetConfigurationResponse> GetConfiguration(GetConfigurationRequest request, ServerCallContext context)
    {
        _metrics.Increment("config_get_requests");
        _metrics.Set("last_config_get_unix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _mediator.Send(new GetConfigurationCommand
            {
                ServiceId = (ServiceId)request.ServiceId
            });

            sw.Stop();
            _metrics.Increment("config_get_success");
            _metrics.Add("config_get_duration_ms_total", sw.ElapsedMilliseconds);
            _metrics.Set("last_config_get_items", response.Configurations.Count);
            return response;
        }
        catch
        {
            sw.Stop();
            _metrics.Increment("config_get_errors");
            _metrics.Add("config_get_duration_ms_total", sw.ElapsedMilliseconds);
            throw;
        }
    }

    public override async Task<UpdateConfigurationResponse> UpdateConfiguration(UpdateConfigurationRequest request, ServerCallContext context)
    {
        _metrics.Increment("config_update_requests");
        _metrics.Set("last_config_update_unix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _mediator.Send(new UpdateConfigurationCommand
            {
                Section = request.Section,
                Key = request.Key,
                Value = request.Value,
                ServiceId = request.ServiceId,
                EditedBy = request.EditedBy,
                EditedFrom = request.EditedFrom
            });

            sw.Stop();
            // handler ловит исключения сам и возвращает Success=false — учитываем это
            if (response.Success)
                _metrics.Increment("config_update_success");
            else
                _metrics.Increment("config_update_errors");

            _metrics.Add("config_update_duration_ms_total", sw.ElapsedMilliseconds);
            return response;
        }
        catch
        {
            sw.Stop();
            _metrics.Increment("config_update_errors");
            _metrics.Add("config_update_duration_ms_total", sw.ElapsedMilliseconds);
            throw;
        }
    }

    // ─── Reserved Names ─────────────────────────────────────────────────────────

    public override async Task<GetReservedNamesResponse> GetReservedNames(GetReservedNamesRequest request, ServerCallContext context)
    {
        _metrics.Increment("reserved_names_get_requests");
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _mediator.Send(new GetReservedNamesCommand());
            sw.Stop();
            _metrics.Increment("reserved_names_get_success");
            _metrics.Add("reserved_names_get_duration_ms_total", sw.ElapsedMilliseconds);
            return response;
        }
        catch
        {
            sw.Stop();
            _metrics.Increment("reserved_names_get_errors");
            _metrics.Add("reserved_names_get_duration_ms_total", sw.ElapsedMilliseconds);
            throw;
        }
    }

    public override async Task<AddReservedNameResponse> AddReservedName(AddReservedNameRequest request, ServerCallContext context)
    {
        _metrics.Increment("reserved_names_add_requests");
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _mediator.Send(new AddReservedNameCommand { Name = request.Name });
            sw.Stop();
            if (response.Success)
                _metrics.Increment("reserved_names_add_success");
            else
                _metrics.Increment("reserved_names_add_errors");
            _metrics.Add("reserved_names_add_duration_ms_total", sw.ElapsedMilliseconds);
            return response;
        }
        catch
        {
            sw.Stop();
            _metrics.Increment("reserved_names_add_errors");
            _metrics.Add("reserved_names_add_duration_ms_total", sw.ElapsedMilliseconds);
            throw;
        }
    }

    public override async Task<UpdateReservedNameResponse> UpdateReservedName(UpdateReservedNameRequest request, ServerCallContext context)
    {
        _metrics.Increment("reserved_names_update_requests");
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _mediator.Send(new UpdateReservedNameCommand { OldName = request.OldName, NewName = request.NewName });
            sw.Stop();
            if (response.Success)
                _metrics.Increment("reserved_names_update_success");
            else
                _metrics.Increment("reserved_names_update_errors");
            _metrics.Add("reserved_names_update_duration_ms_total", sw.ElapsedMilliseconds);
            return response;
        }
        catch
        {
            sw.Stop();
            _metrics.Increment("reserved_names_update_errors");
            _metrics.Add("reserved_names_update_duration_ms_total", sw.ElapsedMilliseconds);
            throw;
        }
    }

    public override async Task<DeleteReservedNameResponse> DeleteReservedName(DeleteReservedNameRequest request, ServerCallContext context)
    {
        _metrics.Increment("reserved_names_delete_requests");
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _mediator.Send(new DeleteReservedNameCommand { Name = request.Name });
            sw.Stop();
            if (response.Success)
                _metrics.Increment("reserved_names_delete_success");
            else
                _metrics.Increment("reserved_names_delete_errors");
            _metrics.Add("reserved_names_delete_duration_ms_total", sw.ElapsedMilliseconds);
            return response;
        }
        catch
        {
            sw.Stop();
            _metrics.Increment("reserved_names_delete_errors");
            _metrics.Add("reserved_names_delete_duration_ms_total", sw.ElapsedMilliseconds);
            throw;
        }
    }

    public override async Task<GetAllConfigurationsResponse> GetAllConfigurations(
        GetAllConfigurationsRequest request,
        ServerCallContext context)
    {
        var response = new GetAllConfigurationsResponse();
        response.Configurations.AddRange((await _configurationStorage.GetAllAsync()).Select(ToProto));
        return response;
    }

    public override async Task<GetConfigurationHistoryResponse> GetConfigurationHistory(
        GetConfigurationHistoryRequest request,
        ServerCallContext context)
    {
        var serviceId = ParseServiceId(request.ServiceId);
        var entry = SettingsCatalog.Resolve(serviceId, request.Section, request.Key);
        var revisions = await _configurationStorage.GetHistoryAsync(
            request.Section,
            request.Key,
            serviceId,
            request.Count <= 0 ? 50 : request.Count,
            context.CancellationToken);
        var response = new GetConfigurationHistoryResponse();
        response.Revisions.AddRange(revisions.Select(revision => new ConfigurationRevision
        {
            Id = revision.Id,
            Section = entry.Section,
            Key = entry.Key,
            ServiceId = (int)serviceId,
            PreviousValue = revision.PreviousValue,
            NewValue = revision.NewValue,
            ChangedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(AsUtc(revision.ChangedAt)),
            ChangedBy = revision.ChangedBy,
            ChangedFrom = revision.ChangedFrom,
            ChangeKind = revision.ChangeKind,
            SourceRevisionId = revision.SourceRevisionId ?? 0,
            HasSourceRevision = revision.SourceRevisionId.HasValue,
            IsSensitive = entry.IsSensitive,
            PreviousHasValue = !string.IsNullOrEmpty(revision.PreviousValue),
            NewHasValue = !string.IsNullOrEmpty(revision.NewValue)
        }));
        return response;
    }

    public override async Task<UpdateConfigurationResponse> RollbackConfiguration(
        RollbackConfigurationRequest request,
        ServerCallContext context)
    {
        try
        {
            await _configurationStorage.RollbackAsync(
                request.RevisionId,
                request.EditedBy,
                request.EditedFrom,
                context.CancellationToken);
            return new UpdateConfigurationResponse { Success = true, Message = "Настройка восстановлена" };
        }
        catch (Exception exception)
        {
            return new UpdateConfigurationResponse { Success = false, Message = exception.Message };
        }
    }

    public override async Task<GetStorageProfilesResponse> GetStorageProfiles(
        GetStorageProfilesRequest request,
        ServerCallContext context)
    {
        var profiles = await _storageProfiles.GetAllAsync(context.CancellationToken);
        var response = new GetStorageProfilesResponse();
        response.Profiles.AddRange(profiles.Select(ToProto));
        if (request.IncludeRevisions)
        {
            foreach (var profile in profiles)
            {
                var revisions = await _storageProfiles.GetHistoryAsync(profile.ProfileId, 100, context.CancellationToken);
                response.Revisions.AddRange(revisions.Select(revision => new StorageProfileRevisionItem
                {
                    Id = revision.Id,
                    ProfileId = revision.ProfileId,
                    ChangedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(AsUtc(revision.ChangedAt)),
                    ChangedBy = revision.ChangedBy,
                    ChangedFrom = revision.ChangedFrom,
                    ChangeKind = revision.ChangeKind,
                    SourceRevisionId = revision.SourceRevisionId ?? 0,
                    HasSourceRevision = revision.SourceRevisionId.HasValue
                }));
            }
        }
        return response;
    }

    public override async Task<SaveStorageProfileResponse> SaveStorageProfile(
        SaveStorageProfileRequest request,
        ServerCallContext context)
    {
        try
        {
            var profile = await _storageProfiles.SaveAsync(new StorageProfileInput(
                request.Role,
                request.ServiceUrl,
                request.AccessKey,
                request.SecretKey,
                request.BucketName,
                request.IsR2,
                request.IsLegacy,
                string.IsNullOrWhiteSpace(request.ProfileId) ? null : request.ProfileId,
                request.ConfirmLegacyMutation), request.EditedBy, request.EditedFrom, context.CancellationToken);
            return new SaveStorageProfileResponse
            {
                Success = true,
                Message = "S3-профиль сохранён",
                Profile = ToProto(profile)
            };
        }
        catch (Exception exception)
        {
            return new SaveStorageProfileResponse { Success = false, Message = exception.Message };
        }
    }

    public override async Task<UpdateConfigurationResponse> ActivateStorageProfile(
        ActivateStorageProfileRequest request,
        ServerCallContext context)
    {
        try
        {
            await _storageProfiles.ActivateAsync(
                request.ProfileId, request.EditedBy, request.EditedFrom, context.CancellationToken);
            return new UpdateConfigurationResponse { Success = true, Message = "S3-профиль активирован" };
        }
        catch (Exception exception)
        {
            return new UpdateConfigurationResponse { Success = false, Message = exception.Message };
        }
    }

    public override async Task<UpdateConfigurationResponse> DisableStorageRole(
        DisableStorageRoleRequest request,
        ServerCallContext context)
    {
        try
        {
            await _storageProfiles.DisableRoleAsync(
                request.Role, request.EditedBy, request.EditedFrom, context.CancellationToken);
            return new UpdateConfigurationResponse { Success = true, Message = "Специализированная роль отключена" };
        }
        catch (Exception exception)
        {
            return new UpdateConfigurationResponse { Success = false, Message = exception.Message };
        }
    }

    private static BarkCloud.Proto.Configuration.ConfigurationItem ToProto(
        BarkCloud.Configuration.Domain.ConfigurationItem item)
    {
        var entry = SettingsCatalog.Resolve(item.ServiceId, item.Section, item.Key);
        var result = new BarkCloud.Proto.Configuration.ConfigurationItem
        {
            Section = item.Section,
            Key = item.Key,
            Value = item.Value,
            EditedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(AsUtc(item.EditedAt)),
            EditedBy = item.EditedBy,
            EditedFrom = item.EditedFrom,
            ServiceId = (int)item.ServiceId,
            IsSensitive = entry.IsSensitive,
            HasValue = !string.IsNullOrEmpty(item.Value),
            IsReadOnly = entry.IsEnvironmentManaged,
            ValueKind = entry.ValueKind.ToString().ToLowerInvariant()
        };
        result.RestartTargets.AddRange(entry.RestartTargets);
        return result;
    }

    private static StorageProfileItem ToProto(DomainStorageProfile profile) => new()
    {
        ProfileId = profile.ProfileId,
        Role = profile.Role,
        Version = profile.Version,
        ServiceUrl = profile.ServiceUrl,
        AccessKey = profile.AccessKey,
        SecretKey = profile.SecretKey,
        BucketName = profile.BucketName,
        IsR2 = profile.IsR2,
        IsActive = profile.IsActive,
        IsLegacy = profile.IsLegacy,
        CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(AsUtc(profile.CreatedAt)),
        CreatedBy = profile.CreatedBy,
        CreatedFrom = profile.CreatedFrom,
        EditedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(AsUtc(profile.EditedAt)),
        EditedBy = profile.EditedBy,
        EditedFrom = profile.EditedFrom,
        HasSecretKey = !string.IsNullOrEmpty(profile.SecretKey)
    };

    private static ServiceId ParseServiceId(int value) =>
        Enum.IsDefined(typeof(ServiceId), value)
            ? (ServiceId)value
            : throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown service id {value}."));

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
