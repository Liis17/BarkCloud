using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using System.Net;
using System.Text;
using System.Threading.Channels;

namespace BarkCloud.GrpcServer.Tests.Logging;

internal sealed class FakeHttpEndpoint : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly TimeSpan _responseDelay;
    private readonly Channel<FakeRequest> _requests = Channel.CreateUnbounded<FakeRequest>();

    private FakeHttpEndpoint(WebApplication application, TimeSpan responseDelay)
    {
        _application = application;
        _responseDelay = responseDelay;
    }

    public string Address { get; private set; } = string.Empty;
    public ChannelReader<FakeRequest> Requests => _requests.Reader;

    public static async Task<FakeHttpEndpoint> StartAsync(TimeSpan? responseDelay = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var application = builder.Build();
        var endpoint = new FakeHttpEndpoint(application, responseDelay ?? TimeSpan.Zero);
        application.Run(endpoint.HandleAsync);
        await application.StartAsync();
        endpoint.Address = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return endpoint;
    }

    public async Task<FakeRequest> ReadAsync(TimeSpan timeout)
        => await Requests.ReadAsync().AsTask().WaitAsync(timeout);

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
        _requests.Writer.TryComplete();
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        await _requests.Writer.WriteAsync(new FakeRequest(
            context.Request.Method,
            context.Request.Path + context.Request.QueryString,
            body), context.RequestAborted);

        if (_responseDelay > TimeSpan.Zero)
            await Task.Delay(_responseDelay, context.RequestAborted);

        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.ContentLength = 0;
    }
}

internal sealed record FakeRequest(string Method, string Path, string Body);
