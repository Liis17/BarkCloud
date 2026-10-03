using BarkCloud.GrpcServer;
using BarkCloud.Proto.Configuration;
using BarkCloud.Proto.Files;

using Grpc.Core;

namespace BarkCloud.Web.Infrastructure;

/// <summary>The service boundary for atomic configuration changes and the durable Files barrier.</summary>
public interface IStorageMigrationControl
{
    Task<IReadOnlyList<StorageProfileItem>> GetProfilesAsync(CancellationToken ct);
    Task PreflightAsync(CancellationToken ct);
    Task<StorageCutoverStatus> BeginAsync(string id, MigrationSource source, MigrationConnection target, CancellationToken ct);
    Task<StorageCutoverStatus> FreezeAsync(string id, CancellationToken ct);
    Task MarkApplyingAsync(string id, CancellationToken ct);
    Task CancelAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<StorageCutoverStatus>> GetCutoversAsync(CancellationToken ct);
    Task RelocateAsync(string id, MigrationSource source, MigrationConnection target, string actor, CancellationToken ct);
    Task RestartAndVerifyAsync(string id, CancellationToken ct);
}

public sealed class StorageMigrationControl(ConfigurationManagementGateway configuration,
    FilesServerApi.FilesServerApiClient files, IDockerDeployment docker, DeploymentJobService deployments)
    : IStorageMigrationControl
{
    public Task<IReadOnlyList<StorageProfileItem>> GetProfilesAsync(CancellationToken ct) => configuration.GetStorageConnectionsAsync(ct);

    public async Task PreflightAsync(CancellationToken ct)
    {
        var result = await docker.PreflightAsync(["files"], false, ct);
        if (!result.Success || result.MissingServices.Contains("files", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Автоматический перезапуск Files недоступен. Проверьте раздел «Обслуживание».");
    }

    public async Task<StorageCutoverStatus> BeginAsync(string id, MigrationSource source, MigrationConnection target, CancellationToken ct)
    {
        var request = new StorageCutoverRequest
        {
            MigrationId = id, SourceServiceUrl = source.ServiceUrl, SourceBucketName = source.BucketName,
            TargetServiceUrl = target.ServiceUrl, TargetBucketName = target.BucketName,
            TargetRegion = target.Region, TargetForcePathStyle = target.ForcePathStyle, TargetIsR2 = target.IsR2,
            TargetConnectionHash = S3Endpoint.ConnectionHash(target.ServiceUrl, target.BucketName, target.AccessKey, target.SecretKey,
                target.Region, target.ForcePathStyle, target.IsR2)
        };
        request.ProfileIds.AddRange(source.ProfileIds);
        return await files.BeginStorageCutoverAsync(request, cancellationToken: ct);
    }

    public async Task<StorageCutoverStatus> FreezeAsync(string id, CancellationToken ct) =>
        await files.FreezeStorageCutoverAsync(new StorageCutoverIdRequest { MigrationId = id }, cancellationToken: ct);

    public async Task MarkApplyingAsync(string id, CancellationToken ct) =>
        await files.MarkStorageCutoverApplyingAsync(new StorageCutoverIdRequest { MigrationId = id }, cancellationToken: ct);

    public async Task CancelAsync(string id, CancellationToken ct) =>
        await files.CancelStorageCutoverAsync(new StorageCutoverIdRequest { MigrationId = id }, cancellationToken: ct);

    public async Task<IReadOnlyList<StorageCutoverStatus>> GetCutoversAsync(CancellationToken ct) =>
        (await files.GetStorageCutoversAsync(new StorageCutoverIdRequest(), cancellationToken: ct)).Cutovers.ToArray();

    public Task RelocateAsync(string id, MigrationSource source, MigrationConnection target, string actor, CancellationToken ct) =>
        configuration.RelocateStorageAsync(source.ServiceUrl, source.BucketName, source.ProfileIds, target.ToProto(), id, actor, ct);

    public async Task RestartAndVerifyAsync(string id, CancellationToken ct)
    {
        var restart = deployments.EnqueueRestart(["files"]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        while (restart.State is DeploymentJobState.Queued or DeploymentJobState.Running)
            await Task.Delay(500, deadline.Token);
        if (restart.State != DeploymentJobState.Completed)
            throw new InvalidOperationException("Не удалось перезапустить Files. Запись остаётся заблокированной; повторите переключение.");
        while (true)
        {
            try
            {
                var response = await files.GetStorageCutoversAsync(new StorageCutoverIdRequest { MigrationId = id },
                    deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: deadline.Token);
                if (response.Cutovers.SingleOrDefault()?.State == "applied") return;
            }
            catch (RpcException e) when (e.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded) { }
            await Task.Delay(1000, deadline.Token);
        }
    }
}
