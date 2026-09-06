using BarkCloud.Web.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class DeploymentJobServiceTests
{
    private static DeploymentJobService CreateService(FakeDocker docker)
        => new(
            docker,
            new DeploymentJobOptions
            {
                InitialSettleDelay = TimeSpan.Zero,
                HealthPollInterval = TimeSpan.Zero,
                HealthTimeout = TimeSpan.FromSeconds(1),
            },
            NullLogger<DeploymentJobService>.Instance);

    private static async Task<DeploymentJob> WaitForTerminalAsync(DeploymentJobService service, Guid id)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = service.GetJob(id);
            if (job is { State: DeploymentJobState.Completed or DeploymentJobState.Failed }) return job;
            await Task.Delay(5);
        }

        throw new TimeoutException("Задача обслуживания не завершилась в тестовый срок");
    }

    [Fact]
    public async Task Update_ProcessesServicesInSafeOrderAndWaitsForRunningState()
    {
        var docker = new FakeDocker();
        var service = CreateService(docker);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var queued = service.EnqueueUpdate(["users", "configuration"]);
            var job = await WaitForTerminalAsync(service, queued.Id);

            job.State.Should().Be(DeploymentJobState.Completed);
            job.Steps.Select(step => step.Service).Should().Equal("configuration", "users");
            docker.Calls.Should().ContainInOrder(
                "pull:configuration",
                "up:configuration",
                "pull:users",
                "up:users",
                "prune");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Update_WhenContainerIsUnhealthy_RollsBackPreviousImage()
    {
        var docker = new FakeDocker { State = (_, _) => ("running", "unhealthy") };
        var service = CreateService(docker);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var queued = service.EnqueueUpdate(["users"]);
            var job = await WaitForTerminalAsync(service, queued.Id);

            job.State.Should().Be(DeploymentJobState.Failed);
            job.Steps.Single().RolledBack.Should().BeTrue();
            job.Error.Should().Contain("откат");
            docker.Calls.Should().ContainInOrder(
                "pull:users",
                "up:users",
                "tag:sha256:old:registry/users:latest",
                "up:users",
                "prune");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EnqueueAll_SkipsMissingOptionalServicesAndWeb()
    {
        var docker = new FakeDocker
        {
            Snapshot = new ServicesSnapshot(
            [
                new ServiceStatus("configuration", "cloud-configuration", "running", "Up", "image", false),
                new ServiceStatus("identity", "cloud-identity", "not_found", "Not found", "", false),
                new ServiceStatus("torrent", "cloud-torrent", "exited", "Exited", "image", false),
                new ServiceStatus("web", "cloud-web", "running", "Up", "image", true),
            ],
            true,
            null),
        };
        var service = CreateService(docker);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var queued = await service.EnqueueAllAsync(DeploymentJobKind.Update);

            queued.Steps.Select(step => step.Service).Should().Equal("configuration", "torrent");
            var job = await WaitForTerminalAsync(service, queued.Id);
            job.State.Should().Be(DeploymentJobState.Completed);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeDocker : IDockerDeployment
    {
        public List<string> Calls { get; } = [];

        public ServicesSnapshot Snapshot { get; set; } = new([], true, null);

        public Func<string, int, (string State, string Health)> State { get; init; }
            = (_, _) => ("running", "none");

        private readonly Dictionary<string, int> _inspectionCounts = new(StringComparer.OrdinalIgnoreCase);

        public Task<ServicesSnapshot> GetServicesStatusAsync() => Task.FromResult(Snapshot);

        public Task<ServiceActionResult> RestartServiceAsync(string service)
        {
            Calls.Add($"restart:{service}");
            return Task.FromResult(new ServiceActionResult(true, "ok"));
        }

        public Task<ServiceActionResult> StartServiceAsync(string service)
        {
            Calls.Add($"start:{service}");
            return Task.FromResult(new ServiceActionResult(true, "ok"));
        }

        public Task<ServiceActionResult> StopServiceAsync(string service)
        {
            Calls.Add($"stop:{service}");
            return Task.FromResult(new ServiceActionResult(true, "ok"));
        }

        public Task ComposePullAsync(string service)
        {
            Calls.Add($"pull:{service}");
            return Task.CompletedTask;
        }

        public Task ComposeUpAsync(string service)
        {
            Calls.Add($"up:{service}");
            return Task.CompletedTask;
        }

        public Task PruneImagesAsync()
        {
            Calls.Add("prune");
            return Task.CompletedTask;
        }

        public Task<(string State, string Health)> InspectStateAsync(string container)
        {
            _inspectionCounts.TryGetValue(container, out var count);
            _inspectionCounts[container] = count + 1;
            return Task.FromResult(State(container, count));
        }

        public Task<string?> GetContainerImageIdAsync(string container)
            => Task.FromResult<string?>("sha256:old");

        public Task<string?> GetContainerImageReferenceAsync(string container)
            => Task.FromResult<string?>("registry/users:latest");

        public Task TagImageAsync(string imageId, string reference)
        {
            Calls.Add($"tag:{imageId}:{reference}");
            return Task.CompletedTask;
        }
    }
}
