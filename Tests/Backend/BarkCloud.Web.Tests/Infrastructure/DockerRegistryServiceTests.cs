using BarkCloud.Web.Infrastructure;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using System.Net;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class DockerRegistryServiceTests
{
    [Fact]
    public void ResolveImageReference_UsesComposeForUnrecognisedRuntimeImageId()
    {
        DockerRegistryService.ResolveImageReference(
                "36b99952f177",
                "docker.barkfluff.com/barkcloud-web:latest")
            .Should()
            .Be("docker.barkfluff.com/barkcloud-web:latest");
    }

    [Fact]
    public void ResolveImageReference_PreservesRecognisedRuntimeReference()
    {
        DockerRegistryService.ResolveImageReference(
                "docker.barkfluff.com/barkcloud-web:1.0.1",
                "docker.barkfluff.com/barkcloud-web:latest")
            .Should()
            .Be("docker.barkfluff.com/barkcloud-web:1.0.1");
    }

    [Fact]
    public async Task GetVersionStatusAsync_UsesHighestSemverAndIgnoresNonSemverTags()
    {
        var service = CreateService("{\"name\":\"barkcloud-users-dev\",\"tags\":[\"1.0.9\",\"latest\",\"1.0.10\",\"3331878f5f4f\",\"invalid\"]}");

        var result = await service.GetVersionStatusAsync("docker.barkfluff.com/barkcloud-users-dev:1.0.9");

        result.CurrentVersion.Should().Be("1.0.9");
        result.LatestVersion.Should().Be("1.0.10");
        result.UpdateAvailable.Should().BeTrue();
        result.State.Should().Be(ImageVersionState.Ready);
    }

    [Fact]
    public async Task GetVersionStatusAsync_KeepsNightlyRepository()
    {
        string? requestedPath = null;
        var service = CreateService("{\"tags\":[\"1.0.10\"]}", request => requestedPath = request.RequestUri?.AbsolutePath);

        await service.GetVersionStatusAsync("docker.barkfluff.com/barkcloud-users-nightly:1.0.0");

        requestedPath.Should().Be("/v2/barkcloud-users-nightly/tags/list");
    }

    [Fact]
    public async Task GetVersionStatusAsync_DerivesInstalledVersionFromLatestImageDigest()
    {
        var service = CreateService(request => request.RequestUri?.AbsolutePath switch
        {
            "/v2/barkcloud-users/tags/list" => Response("{\"tags\":[\"1.0.9\",\"1.0.10\",\"latest\"]}"),
            "/v2/barkcloud-users/manifests/1.0.9" => Manifest("sha256:installed"),
            "/v2/barkcloud-users/manifests/1.0.10" => Manifest("sha256:latest"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await service.GetVersionStatusAsync(
            "docker.barkfluff.com/barkcloud-users:latest",
            "docker.barkfluff.com/barkcloud-users@sha256:installed");

        result.CurrentVersion.Should().Be("1.0.9");
        result.LatestVersion.Should().Be("1.0.10");
        result.UpdateAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetVersionStatusAsync_MatchesInstalledDockerConfigDigest()
    {
        var service = CreateService(request => request.RequestUri?.AbsolutePath switch
        {
            "/v2/barkcloud-web/tags/list" => Response("{\"tags\":[\"1.0.4\",\"latest\"]}"),
            "/v2/barkcloud-web/manifests/1.0.4" => ManifestIndex(
                "sha256:index-1.0.4", "sha256:platform-manifest-1.0.4"),
            "/v2/barkcloud-web/manifests/sha256:platform-manifest-1.0.4" => ManifestWithConfig(
                "sha256:platform-manifest-1.0.4", "sha256:installed-config"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var result = await service.GetVersionStatusAsync(
            "docker.barkfluff.com/barkcloud-web:latest",
            "sha256:installed-config");

        result.CurrentVersion.Should().Be("1.0.4");
        result.LatestVersion.Should().Be("1.0.4");
        result.UpdateAvailable.Should().BeFalse();
        result.State.Should().Be(ImageVersionState.Ready);
    }

    [Fact]
    public async Task GetVersionStatusAsync_MatchesIndexDigestWithoutFetchingChildManifest()
    {
        var service = CreateService(request => request.RequestUri?.AbsolutePath switch
        {
            "/v2/barkcloud-web/tags/list" => Response("{\"tags\":[\"1.0.4\"]}"),
            "/v2/barkcloud-web/manifests/1.0.4" => ManifestIndex(
                "sha256:index-1.0.4", "sha256:platform-manifest-1.0.4"),
            "/v2/barkcloud-web/manifests/sha256:platform-manifest-1.0.4" =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var result = await service.GetVersionStatusAsync(
            "docker.barkfluff.com/barkcloud-web:latest",
            "sha256:index-1.0.4");

        result.CurrentVersion.Should().Be("1.0.4");
        result.State.Should().Be(ImageVersionState.Ready);
    }

    [Fact]
    public async Task GetVersionStatusAsync_MatchesShortDockerConfigId()
    {
        var service = CreateService(request => request.RequestUri?.AbsolutePath switch
        {
            "/v2/barkcloud-web/tags/list" => Response("{\"tags\":[\"1.0.4\"]}"),
            "/v2/barkcloud-web/manifests/1.0.4" => ManifestWithConfig(
                "sha256:manifest-1.0.4", "sha256:1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var result = await service.GetVersionStatusAsync(
            "docker.barkfluff.com/barkcloud-web:latest",
            "1234567890ab");

        result.CurrentVersion.Should().Be("1.0.4");
        result.State.Should().Be(ImageVersionState.Ready);
    }

    [Fact]
    public async Task GetVersionStatusAsync_DerivesInstalledVersionFromDigestReference()
    {
        var service = CreateService(request => request.RequestUri?.AbsolutePath switch
        {
            "/v2/barkcloud-users/tags/list" => Response("{\"tags\":[\"1.0.9\",\"1.0.10\"]}"),
            "/v2/barkcloud-users/manifests/1.0.9" => Manifest("sha256:installed"),
            "/v2/barkcloud-users/manifests/1.0.10" => Manifest("sha256:latest"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await service.GetVersionStatusAsync(
            "docker.barkfluff.com/barkcloud-users@sha256:installed");

        result.CurrentVersion.Should().Be("1.0.9");
        result.LatestVersion.Should().Be("1.0.10");
        result.UpdateAvailable.Should().BeTrue();
        result.Branch.Should().Be("master");
    }

    [Theory]
    [InlineData("1.0.9", true)]
    [InlineData("1.0.10", false)]
    [InlineData("1.1.0", false)]
    public async Task GetVersionStatusAsync_ComparesCurrentVersionWithLatest(string currentVersion, bool updateAvailable)
    {
        var service = CreateService("{\"tags\":[\"1.0.10\",\"latest\"]}");

        var result = await service.GetVersionStatusAsync($"docker.barkfluff.com/barkcloud-configuration:{currentVersion}");

        result.LatestVersion.Should().Be("1.0.10");
        result.UpdateAvailable.Should().Be(updateAvailable);
    }

    [Fact]
    public async Task GetVersionStatusAsync_ReturnsUnknownWhenRegistryHasNoSemverTags()
    {
        var service = CreateService("{\"tags\":[\"latest\",\"3331878f5f4f\"]}");

        var result = await service.GetVersionStatusAsync("docker.barkfluff.com/barkcloud-users:latest");

        result.CurrentVersion.Should().BeNull();
        result.LatestVersion.Should().BeNull();
        result.UpdateAvailable.Should().BeNull();
        result.State.Should().Be(ImageVersionState.Unknown);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetVersionStatusAsync_ReturnsUnavailableWhenRegistryFails(HttpStatusCode statusCode)
    {
        var service = CreateService("", responseStatus: statusCode);

        var result = await service.GetVersionStatusAsync("docker.barkfluff.com/barkcloud-users:1.0.9");

        result.UpdateAvailable.Should().BeNull();
        result.State.Should().Be(ImageVersionState.RegistryUnavailable);
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetVersionStatusAsync_ProvidesLatestButKeepsUpdateUnknownWithoutDigest()
    {
        var service = CreateService("{\"tags\":[\"1.0.10\",\"latest\"]}");

        var result = await service.GetVersionStatusAsync("docker.barkfluff.com/barkcloud-users:latest");

        result.LatestVersion.Should().Be("1.0.10");
        result.CurrentVersion.Should().BeNull();
        result.UpdateAvailable.Should().BeNull();
        result.State.Should().Be(ImageVersionState.Unknown);
    }

    private static DockerRegistryService CreateService(
        string body,
        Action<HttpRequestMessage>? inspectRequest = null,
        HttpStatusCode responseStatus = HttpStatusCode.OK)
        => CreateService(_ => new HttpResponseMessage(responseStatus)
        {
            Content = new StringContent(body)
        }, inspectRequest);

    private static DockerRegistryService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler, Action<HttpRequestMessage>? inspectRequest = null)
    {
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            inspectRequest?.Invoke(request);
            return handler(request);
        }))
        {
            BaseAddress = new Uri("https://docker.barkfluff.com")
        };

        return new DockerRegistryService(client, new MemoryCache(new MemoryCacheOptions()), NullLogger<DockerRegistryService>.Instance);
    }

    private static HttpResponseMessage Response(string body) => new()
    {
        Content = new StringContent(body)
    };

    private static HttpResponseMessage Manifest(string digest)
    {
        var response = Response("{}");
        response.Headers.Add("Docker-Content-Digest", digest);
        return response;
    }

    private static HttpResponseMessage ManifestWithConfig(string digest, string configDigest)
    {
        var response = Response($"{{\"config\":{{\"digest\":\"{configDigest}\"}}}}");
        response.Headers.Add("Docker-Content-Digest", digest);
        return response;
    }

    private static HttpResponseMessage ManifestIndex(string digest, string childDigest)
    {
        var response = Response($"{{\"manifests\":[{{\"digest\":\"{childDigest}\",\"platform\":{{\"os\":\"linux\",\"architecture\":\"amd64\"}}}}]}}");
        response.Headers.Add("Docker-Content-Digest", digest);
        return response;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
