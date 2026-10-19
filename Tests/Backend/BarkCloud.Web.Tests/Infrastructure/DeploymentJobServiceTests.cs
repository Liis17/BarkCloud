using System.Collections.Concurrent;
using System.Diagnostics;

using BarkCloud.Web.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class DeploymentJobServiceTests
{
    private static DeploymentJobService CreateService(FakeDocker docker)
        => new(
            docker,
            new ComposeImageService(new ConfigurationBuilder().Build(), NullLogger<ComposeImageService>.Instance),
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
            if (job is { State: DeploymentJobState.Completed or DeploymentJobState.Failed or DeploymentJobState.AwaitingReconnect }) return job;
            await Task.Delay(5);
        }

        throw new TimeoutException("Задача обслуживания не завершилась в тестовый срок");
    }

    [Fact]
    public async Task Update_ProcessesServicesInSafeOrderAndWaitsForRunningState()
    {
        var docker = new FakeDocker();
        docker.ContainerNames["configuration"] = "config-app";
        docker.ContainerNames["users"] = "users-app";
        var service = CreateService(docker);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var queued = service.EnqueueUpdate(["users", "configuration"]);
            var job = await WaitForTerminalAsync(service, queued.Id);

            job.State.Should().Be(DeploymentJobState.Completed);
            job.Steps.Select(step => step.Service).Should().Equal("configuration", "users");
            docker.Calls.Should().ContainInOrder(
                "preflight:update",
                "image-id:config-app",
                "up:configuration",
                "image-id:users-app",
                "up:users",
                "prune");
            docker.Calls.Should().Contain("inspect-state:config-app");
            docker.Calls.Should().Contain("inspect-state:users-app");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task QueuedJobs_RestoreTheirCapturedRequestContexts()
    {
        var docker = new FakeDocker();
        var service = CreateService(docker);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var firstRequest = new Activity("first-request").SetIdFormat(ActivityIdFormat.W3C).Start();
            firstRequest.TraceStateString = "vendor=one";
            var firstParent = firstRequest.Context;
            var first = service.EnqueueUpdate(["users"]);
            firstRequest.Stop();

            var secondRequest = new Activity("second-request").SetIdFormat(ActivityIdFormat.W3C).Start();
            secondRequest.TraceStateString = "vendor=two";
            var secondParent = secondRequest.Context;
            var second = service.EnqueueUpdate(["files"]);
            secondRequest.Stop();

            (await WaitForTerminalAsync(service, first.Id)).State.Should().Be(DeploymentJobState.Completed);
            (await WaitForTerminalAsync(service, second.Id)).State.Should().Be(DeploymentJobState.Completed);

            var captured = docker.OperationContexts.ToArray();
            captured.Should().HaveCount(2);
            captured[0].Context.TraceId.Should().Be(firstParent.TraceId);
            captured[0].ParentSpanId.Should().Be(firstParent.SpanId);
            captured[0].Context.TraceState.Should().Be(firstParent.TraceState);
            captured[1].Context.TraceId.Should().Be(secondParent.TraceId);
            captured[1].ParentSpanId.Should().Be(secondParent.SpanId);
            captured[1].Context.TraceState.Should().Be(secondParent.TraceState);
            captured[0].Context.TraceId.Should().NotBe(captured[1].Context.TraceId);
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
                "preflight:update",
                "up:users",
                "tag:sha256:old:registry/users:latest",
                "up:users");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EnqueueAll_SkipsMissingOptionalServicesAndKeepsWebLast()
    {
        var docker = new FakeDocker();
        docker.MissingServices.Add("notification");
        var service = CreateService(docker);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var queued = await service.EnqueueAllAsync(DeploymentJobKind.Restart);

            queued.Steps.Select(step => step.Service).Should().Equal(
                "configuration", "identity", "users", "files", "notification", "torrent", "web");
            var job = await WaitForTerminalAsync(service, queued.Id);
            job.State.Should().Be(DeploymentJobState.AwaitingReconnect);
            job.RequiresReconnect.Should().BeTrue();
            job.Steps.Single(step => step.Service == "notification").State.Should().Be(DeploymentStepState.Skipped);
            docker.Calls.Should().ContainInOrder(
                "preflight:restart",
                "restart:configuration",
                "restart:users",
                "restart:files",
                "restart:torrent",
                "web:restart");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task UpdateAll_WhenPreflightFails_ReportsOneErrorAndSkipsEveryStep()
    {
        var docker = new FakeDocker { FailPreflight = true };
        var service = CreateService(docker);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var queued = await service.EnqueueAllAsync(DeploymentJobKind.Update);
            var job = await WaitForTerminalAsync(service, queued.Id);

            job.State.Should().Be(DeploymentJobState.Failed);
            job.Error.Should().Be("preflight failed");
            job.Diagnostic.Should().Be("docker compose config failed");
            job.Steps.Should().OnlyContain(step => step.State == DeploymentStepState.Skipped);
            job.Steps.Should().OnlyContain(step => step.Diagnostic == null);
            docker.Calls.Should().Equal("preflight:update");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeDocker : IDockerDeployment
    {
        public List<string> Calls { get; } = [];
        public ConcurrentQueue<(ActivityContext Context, ActivitySpanId ParentSpanId)> OperationContexts { get; } = new();

        public Dictionary<string, string> ContainerNames { get; } = DockerContainerNames.DefaultValues.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

        public ServicesSnapshot Snapshot { get; set; } = new([], true, null);

        public string ContainerNameFor(string service) => ContainerNames[service];

        public Func<string, int, (string State, string Health)> State { get; init; }
            = (_, _) => ("running", "none");

        public HashSet<string> MissingServices { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FailPreflight { get; set; }

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

        public Task<DockerPreflightResult> PreflightAsync(IEnumerable<string> services, bool pullImages, CancellationToken cancellationToken = default)
        {
            if (Activity.Current is { } activity)
                OperationContexts.Enqueue((activity.Context, activity.ParentSpanId));
            Calls.Add(pullImages ? "preflight:update" : "preflight:restart");
            if (FailPreflight)
                return Task.FromResult(new DockerPreflightResult(false, new HashSet<string>(), [], "preflight failed", "docker compose config failed"));

            var requested = services.ToList();
            return Task.FromResult(new DockerPreflightResult(
                true,
                new HashSet<string>(requested.Select(DockerService.ComposeServiceNameFor), StringComparer.OrdinalIgnoreCase),
                MissingServices.ToList(),
                null,
                null));
        }

        public Task PruneImagesAsync()
        {
            Calls.Add("prune");
            return Task.CompletedTask;
        }

        public Task<(string State, string Health)> InspectStateAsync(string container)
        {
            Calls.Add($"inspect-state:{container}");
            _inspectionCounts.TryGetValue(container, out var count);
            _inspectionCounts[container] = count + 1;
            return Task.FromResult(State(container, count));
        }

        public Task<string?> GetContainerImageIdAsync(string container)
        {
            Calls.Add($"image-id:{container}");
            return Task.FromResult<string?>("sha256:old");
        }

        public Task<string?> GetContainerImageReferenceAsync(string container)
            => Task.FromResult<string?>("registry/users:latest");

        public Task TagImageAsync(string imageId, string reference)
        {
            Calls.Add($"tag:{imageId}:{reference}");
            return Task.CompletedTask;
        }

        public Task<string?> GetComposeImageReferenceAsync(string service)
            => Task.FromResult<string?>("docker.barkfluff.com/barkcloud-web:latest");

        public Task<ServiceActionResult> UpdateWebSelfAsync(string? targetImage = null, string? operationId = null)
        {
            Calls.Add("web:update");
            return Task.FromResult(new ServiceActionResult(true, "started"));
        }

        public Task<ServiceActionResult> RestartWebSelfAsync(string? operationId = null)
        {
            Calls.Add("web:restart");
            return Task.FromResult(new ServiceActionResult(true, "started"));
        }
    }
}
