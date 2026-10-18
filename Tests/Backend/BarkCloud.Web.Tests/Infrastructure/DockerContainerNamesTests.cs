using System.ComponentModel;
using System.Diagnostics;
using BarkCloud.Web.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Sdk;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class DockerContainerNamesTests
{
    private const string OperationId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string NormalizedOperationId = "aaaaaaaabbbbccccddddeeeeeeeeeeee";
    private const string WebContainer = "app";
    private const string BackupContainer = $"{WebContainer}-{NormalizedOperationId}-bak";
    private const string OldContainerId = "1234567890123456789012345678901234567890123456789012345678901234";

    [Fact]
    public void Defaults_PreserveExistingContainerNames()
    {
        var names = DockerContainerNames.FromConfiguration(new ConfigurationBuilder().Build());

        names.For("configuration").Should().Be("cloud-configuration");
        names.For("identity").Should().Be("cloud-identity");
        names.For("users").Should().Be("cloud-users");
        names.For("files").Should().Be("cloud-files");
        names.For("notification").Should().Be("cloud-notification");
        names.For("torrent").Should().Be("cloud-torrent");
        names.For("web").Should().Be("cloud-web");
    }

    [Fact]
    public void CustomNames_AreAcceptedWhenUniqueAndDoNotShadowFixedDnsAliases()
    {
        var names = DockerContainerNames.FromConfiguration(Configuration(
            ("web", WebContainer),
            ("identity", "app-bak")));

        names.For("web").Should().Be(WebContainer);
        names.For("identity").Should().Be("app-bak");
        DockerContainerNames.DefaultValues["configuration"].Should().Be("cloud-configuration");
    }

    [Fact]
    public async Task Statuses_ResolveCustomNamesAndKeepComposeKeys()
    {
        var service = CreateDockerService(new RecordingDockerRunner());

        var snapshot = await service.GetServicesStatusAsync(includeImageDigests: false);

        var web = snapshot.Services.Single(item => item.Service == "web");
        web.Container.Should().Be(WebContainer);
        web.State.Should().Be("running");
        web.ComposeService.Should().Be("cloud-web");
        var identity = snapshot.Services.Single(item => item.Service == "identity");
        identity.Container.Should().Be("app-bak");
        identity.State.Should().Be("running");
        identity.ComposeService.Should().Be("cloud-identity");
    }

    [Theory]
    [InlineData("web", "bad/name")]
    [InlineData("web", "a")]
    [InlineData("web", "cloud-nginx")]
    [InlineData("web", "cloud-identity")]
    [InlineData("web", "app\n")]
    public void InvalidOrConflictingNames_AreRejected(string service, string name)
    {
        var act = () => DockerContainerNames.FromConfiguration(Configuration((service, name)));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DuplicateNames_AreRejectedIgnoringCase()
    {
        var act = () => DockerContainerNames.FromConfiguration(Configuration(
            ("web", "app"),
            ("identity", "APP")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*назначено нескольким сервисам*");
    }

    [Fact]
    public async Task SelfUpdate_UsesCustomWebNameAndOwnsItsOperationContainers()
    {
        var runner = new RecordingDockerRunner();
        var service = CreateDockerService(runner);

        var result = await service.UpdateWebSelfAsync("docker.barkfluff.com/barkcloud-web:1.13.0", OperationId);

        result.Success.Should().BeTrue();
        var helperRun = runner.Calls.Single(args => args[0] == "run");
        var helperName = helperRun[Array.IndexOf(helperRun, "--name") + 1];
        helperName.Should().Contain(NormalizedOperationId);
        helperName.Should().Contain("updater");
        var script = helperRun[^1];

        runner.Calls.Any(args => args.SequenceEqual(new[] { "inspect", WebContainer })).Should().BeTrue();
        runner.Calls.Any(args => args.SequenceEqual(new[] { "inspect", BackupContainer })).Should().BeTrue();
        script.Should().Contain($"web_container='{WebContainer}'");
        script.Should().Contain($"backup_container='{BackupContainer}'");
        script.Should().Contain($"'--name' '{WebContainer}'");
        script.Should().Contain($"'com.barkcloud.maintenance-operation={NormalizedOperationId}'");
        script.Should().NotContain("com.barkcloud.maintenance-operation=previous-operation");
        script.Should().Contain("docker inspect --format '{{.Id}}' \"$1\"");
        script.Should().Contain("docker cp '/app/maintenance/last-operation.json' \"$web_container:/app/maintenance/last-operation.json\"");
        script.Should().Contain("docker rename \"$web_container\" \"$backup_container\"");
        script.Should().Contain("'docker' 'network' 'connect' '--alias' 'cloud-web' 'shared-barkcloud' 'app'");
        script.Should().Contain("[ \"$current_id\" = \"$new_web_id\" ] && [ \"$owned_id\" = \"$new_web_id\" ]");
        script.Should().Contain("[ \"$backup_id\" != \"$old_container_id\" ]");
        script.Should().NotContain("app-bak");
        script.Should().NotContain("nginx");

        script.Should().NotContain("run_logged docker rm -f \"$backup_container\" || true\nif run_logged docker rename");
        runner.Calls.Any(args => args[0] == "rm").Should().BeFalse();
        DockerService.ShQuote("name'withquote").Should().Be("'name'\\''withquote'");
    }

    [Fact]
    public async Task SelfUpdate_PreservesAgentMountsAndLifecycleOptionsForNewAndRollbackRuns()
    {
        var runner = new RecordingDockerRunner();
        var service = CreateDockerService(runner);

        var result = await service.UpdateWebSelfAsync("new-web-image", OperationId);

        result.Success.Should().BeTrue();
        var script = runner.Calls.Single(args => args[0] == "run")[^1];
        script.Split("'--init'", StringSplitOptions.None).Length.Should().Be(3);
        script.Split("'--stop-timeout'", StringSplitOptions.None).Length.Should().Be(3);
        script.Split("'--stop-signal'", StringSplitOptions.None).Length.Should().Be(3);
        script.Split("'LOGNARA_SERVICE=app'", StringSplitOptions.None).Length.Should().Be(3);
        script.Split("'LOGNARA_TOKEN=fixture-agent-token'", StringSplitOptions.None).Length.Should().Be(3);
        script.Split(
            "'--mount' 'type=volume,source=barkcloud_lognara_app_spool,destination=/var/lib/lognara/spool'",
            StringSplitOptions.None).Length.Should().Be(3);
        script.Split("'--stop-timeout' '75'", StringSplitOptions.None).Length.Should().Be(3);
        script.Split("'--stop-signal' 'SIGTERM'", StringSplitOptions.None).Length.Should().Be(3);
        script.Should().Contain("run_logged docker stop \"$backup_container\" || true");
        script.Should().NotContain("docker stop -t ");
    }

    [Fact]
    public async Task SelfUpdate_LegacyInspectWithoutLifecycleOptionsOmitsRunFlags()
    {
        var runner = new RecordingDockerRunner();
        runner.UseLegacyLifecycleOptions();
        var service = CreateDockerService(runner);

        var result = await service.UpdateWebSelfAsync("new-web-image", OperationId);

        result.Success.Should().BeTrue();
        var script = runner.Calls.Single(args => args[0] == "run")[^1];
        script.Should().NotContain("'--init'");
        script.Should().NotContain("'--stop-timeout'");
        script.Should().NotContain("'--stop-signal'");
        script.Should().Contain("run_logged docker stop \"$backup_container\" || true");
        script.Should().NotContain("docker stop -t ");
    }

    [Theory]
    [InlineData("helper")]
    [InlineData("backup")]
    public async Task SelfUpdate_WhenOperationNameIsOccupied_FailsWithoutRemovingIt(string occupiedKind)
    {
        var runner = new RecordingDockerRunner();
        var occupiedName = occupiedKind == "helper"
            ? $"{WebContainer}-{NormalizedOperationId}-updater"
            : BackupContainer;
        runner.ExistingNames.Add(occupiedName);
        var service = CreateDockerService(runner);

        var result = await service.UpdateWebSelfAsync("docker.barkfluff.com/barkcloud-web:1.13.0", OperationId);

        result.Success.Should().BeFalse();
        result.ErrorDetails.Should().Contain("уже занято");
        runner.Calls.Any(args => args.SequenceEqual(new[] { "inspect", occupiedName })).Should().BeTrue();
        runner.Calls.Any(args => args[0] == "rm" || args[0] == "run").Should().BeFalse();
    }

    [Fact]
    public async Task SelfUpdate_WhenFailedRunCreatesOperationOwnedContainer_RollsBackWithoutTouchingIdentityName()
    {
        var bash = await GetBashExecutableAsync();
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"barkcloud-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var shellDirectory = ToBashPath(tempDirectory);
            var spec = new DockerService.WebRecreateSpec(
                "new-web-image",
                OldContainerId,
                [
                    "-d", "--name", WebContainer, "--init", "--stop-timeout", "75", "--stop-signal", "SIGTERM",
                    "-e", "LOGNARA_SERVICE=app", "-e", "LOGNARA_TOKEN=fixture-agent-token",
                    "--mount", "type=volume,source=barkcloud_lognara_app_spool,destination=/var/lib/lognara/spool",
                    "--label", $"com.barkcloud.maintenance-operation={NormalizedOperationId}", "new-web-image"
                ],
                [
                    "-d", "--name", WebContainer, "--init", "--stop-timeout", "75", "--stop-signal", "SIGTERM",
                    "-e", "LOGNARA_SERVICE=app", "-e", "LOGNARA_TOKEN=fixture-agent-token",
                    "--mount", "type=volume,source=barkcloud_lognara_app_spool,destination=/var/lib/lognara/spool",
                    "--label", $"com.barkcloud.maintenance-operation={NormalizedOperationId}", OldContainerId
                ],
                ["'docker' 'network' 'connect' '--alias' 'cloud-web' 'shared-barkcloud' 'app'"]);
            var script = DockerService.BuildSelfUpdateScript(
                spec,
                WebContainer,
                BackupContainer,
                NormalizedOperationId,
                $"{shellDirectory}/state.json",
                $"{shellDirectory}/update.log",
                shellDirectory);

            var fakeDocker = """
                state_dir=__STATE_DIR__
                mkdir -p "$state_dir"
                printf '__OLD_CONTAINER_ID__' > "$state_dir/app.id"
                printf 'identity-container-id' > "$state_dir/app-bak.id"
                printf '\n' > "$state_dir/app.label"
                printf '\n' > "$state_dir/app-bak.label"
                printf '0' > "$state_dir/run-count"
                docker() {
                  command="$1"
                  shift
                  case "$command" in
                    inspect)
                      [ "$1" = --format ] || return 1
                      format="$2"
                      name="$3"
                      [ -f "$state_dir/$name.id" ] || return 1
                      case "$format" in
                        '{{.Id}}') cat "$state_dir/$name.id" ;;
                        '{{ index .Config.Labels "com.barkcloud.maintenance-operation" }}') cat "$state_dir/$name.label" ;;
                        '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}')
                          if [ "$(cat "$state_dir/$name.id")" = failed-run-container-id ]; then printf 'exited|none'; else printf 'running|none'; fi
                          ;;
                        *) return 1 ;;
                      esac
                      ;;
                    pull|stop|logs|network|exec|cp|image) return 0 ;;
                    rename)
                      mv "$state_dir/$1.id" "$state_dir/$2.id"
                      mv "$state_dir/$1.label" "$state_dir/$2.label"
                      ;;
                    run)
                      run_number="$(cat "$state_dir/run-count")"
                      run_number=$((run_number + 1))
                      printf '%s' "$run_number" > "$state_dir/run-count"
                      printf '%s\n' "$@" >> "$state_dir/run-args"
                      name=
                      operation=
                      while [ "$#" -gt 0 ]; do
                        case "$1" in
                          --name) name="$2"; shift 2 ;;
                          --label) case "$2" in com.barkcloud.maintenance-operation=*) operation="${2#*=}" ;; esac; shift 2 ;;
                          *) shift ;;
                        esac
                      done
                      if [ "$run_number" = 1 ]; then id=failed-run-container-id; else id=rollback-container-id; fi
                      printf '%s' "$id" > "$state_dir/$name.id"
                      printf '%s' "$operation" > "$state_dir/$name.label"
                      if [ "$run_number" = 1 ]; then return 1; fi
                      printf '%s' "$id"
                      ;;
                    rm)
                      [ "$1" = -f ] && shift
                      name="$1"
                      printf '%s\n' "$name" >> "$state_dir/removed"
                      rm -f "$state_dir/$name.id" "$state_dir/$name.label"
                      ;;
                    *) return 1 ;;
                  esac
                }
                sleep() { :; }
                """;
            fakeDocker = fakeDocker
                .Replace("__STATE_DIR__", DockerService.ShQuote(shellDirectory), StringComparison.Ordinal)
                .Replace("__OLD_CONTAINER_ID__", OldContainerId, StringComparison.Ordinal);

            var bashInput = (fakeDocker + "\n" + script)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal);
            var result = await RunBashAsync(bash, bashInput);

            result.ExitCode.Should().Be(1, result.StandardError);
            File.ReadAllText(Path.Combine(tempDirectory, "app.id")).Should().Be("rollback-container-id");
            File.ReadAllText(Path.Combine(tempDirectory, "app-bak.id")).Should().Be("identity-container-id");
            File.Exists(Path.Combine(tempDirectory, $"{BackupContainer}.id")).Should().BeFalse();
            File.ReadAllLines(Path.Combine(tempDirectory, "removed")).Should().Equal(WebContainer, BackupContainer);
            File.ReadAllText(Path.Combine(tempDirectory, "run-count")).Should().Be("2");
            var runArgs = File.ReadAllLines(Path.Combine(tempDirectory, "run-args"));
            runArgs.Count(argument => argument == "--init").Should().Be(2);
            runArgs.Count(argument => argument == "--stop-timeout").Should().Be(2);
            runArgs.Count(argument => argument == "75").Should().Be(2);
            runArgs.Count(argument => argument == "--stop-signal").Should().Be(2);
            runArgs.Count(argument => argument == "SIGTERM").Should().Be(2);
            runArgs.Count(argument => argument == "LOGNARA_SERVICE=app").Should().Be(2);
            runArgs.Count(argument => argument == "LOGNARA_TOKEN=fixture-agent-token").Should().Be(2);
            runArgs.Count(argument => argument == "type=volume,source=barkcloud_lognara_app_spool,destination=/var/lib/lognara/spool")
                .Should().Be(2);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void BashLauncher_UsesWslOnWindows()
    {
        var (executable, prefixArguments) = GetBashLauncher();

        if (OperatingSystem.IsWindows())
        {
            executable.Should().Be("wsl.exe");
            prefixArguments.Should().Equal("--exec", "bash");
        }
        else
        {
            executable.Should().Be("bash");
            prefixArguments.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Restart_UsesCustomWebNameAndOperationSpecificHelperWithoutProxyCommands()
    {
        var runner = new RecordingDockerRunner();
        var service = CreateDockerService(runner);

        var result = await service.RestartWebSelfAsync(OperationId);

        result.Success.Should().BeTrue();
        var helperRun = runner.Calls.Single(args => args[0] == "run");
        var helperName = helperRun[Array.IndexOf(helperRun, "--name") + 1];
        helperName.Should().Contain(NormalizedOperationId);
        helperName.Should().Contain("restarter");
        helperRun[^1].Should().Contain("web_container='app'");
        helperRun[^1].Should().Contain("run_logged docker restart \"$web_container\"");
        helperRun[^1].Should().NotContain("docker restart -t ");
        helperRun[^1].Should().NotContain("docker restart --timeout ");
        helperRun[^1].Should().NotContain("nginx");
    }

    private static IConfiguration Configuration(params (string Service, string Name)[] names)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(names.ToDictionary(
                pair => $"Docker:ContainerNames:{pair.Service}",
                pair => (string?)pair.Name,
                StringComparer.OrdinalIgnoreCase))
            .Build();

    private static DockerService CreateDockerService(RecordingDockerRunner runner)
    {
        var configuration = Configuration(("web", WebContainer), ("identity", "app-bak"));
        var compose = new ComposeImageService(configuration, NullLogger<ComposeImageService>.Instance);
        var maintenance = new MaintenanceOperationStore(configuration, NullLogger<MaintenanceOperationStore>.Instance);
        return new DockerService(
            NullLogger<DockerService>.Instance,
            compose,
            maintenance,
            configuration,
            runner.RunAsync);
    }

    private static (string Executable, string[] PrefixArguments) GetBashLauncher()
        => OperatingSystem.IsWindows()
            ? ("wsl.exe", ["--exec", "bash"])
            : ("bash", []);

    private static async Task<(string Executable, string[] PrefixArguments)> GetBashExecutableAsync()
    {
        var launcher = GetBashLauncher();
        try
        {
            var startInfo = new ProcessStartInfo(launcher.Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in launcher.PrefixArguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
                throw SkipException.ForSkip(BashUnavailableReason());
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var details = (await standardError).Trim();
                throw SkipException.ForSkip(details.Length == 0
                    ? BashUnavailableReason()
                    : $"{BashUnavailableReason()} {details}");
            }
            await standardOutput;
            await standardError;
            return launcher;
        }
        catch (Win32Exception)
        {
            throw SkipException.ForSkip(BashUnavailableReason());
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunBashAsync(
        (string Executable, string[] PrefixArguments) launcher,
        string script)
    {
        var startInfo = new ProcessStartInfo(launcher.Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in launcher.PrefixArguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("-s");
        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var normalizedScript = script
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        await process.StandardInput.WriteAsync(normalizedScript);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return (process.ExitCode, await standardOutput, await standardError);
    }

    private static string BashUnavailableReason()
        => OperatingSystem.IsWindows()
            ? "WSL bash is unavailable for generated-script verification; install WSL and a default Linux distribution."
            : "Bash is not available for generated-script verification.";

    private static string ToBashPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path;

        var fullPath = Path.GetFullPath(path);
        return $"/mnt/{char.ToLowerInvariant(fullPath[0])}/{fullPath[3..].Replace('\\', '/') }";
    }

    private sealed class RecordingDockerRunner
    {
        private const string WebInspect = """
            [{
              "Id":"1234567890123456789012345678901234567890123456789012345678901234",
              "Image":"sha256:old-image",
              "Config":{
                "Image":"docker.barkfluff.com/barkcloud-web:1.12.0",
                "User":"",
                "StopSignal":"SIGTERM",
                "StopTimeout":75,
                "Env":["ASPNETCORE_ENVIRONMENT=Production","LOGNARA_SERVICE=app","LOGNARA_TOKEN=fixture-agent-token"],
              "Labels":{"com.docker.compose.project":"barkcloud","com.docker.compose.service":"cloud-web","com.barkcloud.maintenance-operation":"previous-operation"}
              },
              "HostConfig":{"Init":true,"RestartPolicy":{"Name":"unless-stopped","MaximumRetryCount":0},"PortBindings":{}},
              "Mounts":[{"Type":"volume","Name":"maintenance","Source":"barkcloud_cloud-web-maintenance","Destination":"/app/maintenance","RW":true},{"Type":"volume","Name":"barkcloud_lognara_app_spool","Source":"/var/lib/docker/volumes/barkcloud_lognara_app_spool/_data","Destination":"/var/lib/lognara/spool","RW":true}],
              "NetworkSettings":{"Networks":{
                "barkcloud-network":{"Aliases":["cloud-web","123456789012"]},
                "shared-barkcloud":{"Aliases":["cloud-web","123456789012"]}
              }}
            }]
            """;

        private string _webInspect = WebInspect;

        public List<string[]> Calls { get; } = [];
        public HashSet<string> ExistingNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void UseLegacyLifecycleOptions()
        {
            _webInspect = WebInspect
                .Replace("\"StopSignal\":\"SIGTERM\",", string.Empty, StringComparison.Ordinal)
                .Replace("\"StopTimeout\":75,", string.Empty, StringComparison.Ordinal)
                .Replace("\"Init\":true,", string.Empty, StringComparison.Ordinal);
        }

        public Task<string> RunAsync(string fileName, string[] args)
        {
            Calls.Add(args.ToArray());
            if (fileName != "docker")
                throw new InvalidOperationException($"Unexpected executable: {fileName}");

            if (args[0] == "ps")
                return Task.FromResult("""
                    {"Names":"app","State":"running","Status":"Up","Image":"web-image"}
                    {"Names":"app-bak","State":"running","Status":"Up","Image":"identity-image"}
                    """);

            if (args[0] == "run")
                return Task.FromResult("helper-container-id");

            if (args[0] == "inspect" && args.Length == 2)
            {
                if (args[1] == WebContainer)
                    return Task.FromResult(_webInspect);
                if (ExistingNames.Contains(args[1]))
                    return Task.FromResult("{}");
                throw NotFound(args);
            }

            if (args[0] == "inspect" && args.Length == 4 && args[1] == "--format")
            {
                var template = args[2];
                if (template == "{{.Config.Image}}")
                    return Task.FromResult("docker.barkfluff.com/barkcloud-web:1.12.0");
                if (template.Contains("{{.Type}}|{{.Source}}|{{.Name}}", StringComparison.Ordinal))
                    return Task.FromResult("volume|barkcloud_cloud-web-maintenance|barkcloud_cloud-web-maintenance");
                if (template.Contains("/var/run/docker.sock", StringComparison.Ordinal))
                    return Task.FromResult("/var/run/docker.sock");
                if (template.Contains("{{.Id}}", StringComparison.Ordinal))
                    return Task.FromResult(OldContainerId);
                return Task.FromResult(string.Empty);
            }

            throw new InvalidOperationException($"Unexpected docker command: {string.Join(' ', args)}");
        }

        private static DockerCommandException NotFound(string[] args)
            => new("docker", args, 1, string.Empty, $"Error: No such object: {args[^1]}");
    }
}
