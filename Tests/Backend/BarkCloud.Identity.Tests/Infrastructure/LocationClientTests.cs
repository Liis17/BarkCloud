using System.Diagnostics;
using System.Net;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Infrastructure;

public class LocationClientTests
{
    private readonly MetricsCollector _metrics = new();

    private LocationClient CreateSut(HttpMessageHandler handler)
        => new(new HttpClient(handler), _metrics, NullLogger<LocationClient>.Instance);

    [Fact]
    public async Task GetLocation_ServerNeverResponds_ReturnsNullWithinTimeout()
    {
        var sut = CreateSut(new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var stopwatch = Stopwatch.StartNew();
        var location = await sut.GetLocation("203.0.113.7");
        stopwatch.Stop();

        location.Should().BeNull();
        stopwatch.Elapsed.Should().BeLessThan(LocationClient.RequestTimeout + TimeSpan.FromSeconds(3));
        _metrics.SnapshotAndReset().Should().ContainKey("geolocation_timeouts");
    }

    [Fact]
    public async Task GetLocationString_ServerNeverResponds_ReturnsDash()
    {
        var sut = CreateSut(new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var text = await sut.GetLocationString("203.0.113.7");

        text.Should().Be("-");
    }

    [Fact]
    public async Task GetLocation_ValidResponse_ReturnsLocation()
    {
        var sut = CreateSut(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"country":"Россия","regionName":"Москва","city":"Москва"}""")
        })));

        var location = await sut.GetLocation("203.0.113.7");

        location.Should().NotBeNull();
        location!.City.Should().Be("Москва");
        _metrics.SnapshotAndReset().Should().NotContainKey("geolocation_timeouts");
    }

    [Fact]
    public async Task GetLocation_ErrorStatus_ReturnsNull()
    {
        var sut = CreateSut(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));

        var location = await sut.GetLocation("203.0.113.7");

        location.Should().BeNull();
        _metrics.SnapshotAndReset().Should().ContainKey("geolocation_errors");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
