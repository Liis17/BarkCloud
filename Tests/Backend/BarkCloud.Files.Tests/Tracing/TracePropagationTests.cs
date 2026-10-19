using System.Diagnostics;
using System.Net;
using System.Threading.Channels;

using BarkCloud.GrpcServer;
using BarkCloud.Proto.Configuration;

using Grpc.Core;
using Grpc.Net.Client;

using MassTransit;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BarkCloud.Files.Tests.Tracing;

[CollectionDefinition("Trace propagation", DisableParallelization = true)]
public sealed class TracePropagationCollection;

[Collection("Trace propagation")]
public sealed class TracePropagationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpGrpcAndMessage_PreserveIncomingTraceWithDistinctSpans(bool recorded)
    {
        var probe = new TraceProbe();
        var frontBuilder = CreateBuilder(HttpProtocols.Http1);
        frontBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConsoleOutput"] = "false", ["SeqEnable"] = "false", ["LognaraEnable"] = "false"
        });
        await using var logging = await frontBuilder.AddBarkCloudSerilogAsync("TracePropagationTest");

        var backBuilder = CreateBuilder(HttpProtocols.Http2);
        backBuilder.Services.AddSingleton(probe);
        backBuilder.Services.AddGrpc();
        backBuilder.Services.AddMassTransit(registration => registration.UsingInMemory((_, bus) =>
            bus.ReceiveEndpoint("trace-probe", endpoint => endpoint.Handler<TraceProbeMessage>(context =>
            {
                probe.Record(context.Message.Id, "consumer");
                return Task.CompletedTask;
            }))));
        await using var backend = backBuilder.Build();
        backend.MapGrpcService<TraceProbeGrpcService>();
        await backend.StartAsync();

        using var channel = GrpcChannel.ForAddress(Address(backend));
        var grpc = new ConfigurationApi.ConfigurationApiClient(channel);
        await using var frontend = frontBuilder.Build();
        frontend.MapGet("/trace/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
        {
            probe.Record(id, "http");
            await grpc.GetReadinessAsync(new GetReadinessRequest(),
                new Metadata { { "probe-id", id.ToString() } }, cancellationToken: cancellationToken);
            return Results.Ok();
        });
        await frontend.StartAsync();

        using var http = new HttpClient();
        var incoming = Enumerable.Range(0, 2).Select(_ =>
            (Id: Guid.NewGuid(), Trace: ActivityTraceId.CreateRandom(), Span: ActivitySpanId.CreateRandom())).ToArray();
        await Task.WhenAll(incoming.Select(async item =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Address(frontend)}/trace/{item.Id}");
            request.Headers.Add("traceparent", $"00-{item.Trace}-{item.Span}-{(recorded ? "01" : "00")}");
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }));

        var stages = new List<TraceStage>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (stages.Count < 6)
            stages.Add(await probe.Stages.Reader.ReadAsync(timeout.Token));

        foreach (var item in incoming)
        {
            var chain = stages.Where(stage => stage.Id == item.Id).ToArray();
            chain.Select(stage => stage.Stage).Should().BeEquivalentTo(["http", "grpc", "consumer"]);
            chain.Should().OnlyContain(stage => stage.Context.TraceId == item.Trace);
            chain.Select(stage => stage.Context.SpanId).Should().OnlyHaveUniqueItems();
            chain.Should().OnlyContain(stage => stage.Context.SpanId != default);
        }
        Activity.Current.Should().BeNull();
    }

    private static WebApplicationBuilder CreateBuilder(HttpProtocols protocol)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = protocol));
        return builder;
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}

public sealed record TraceProbeMessage(Guid Id);
internal sealed record TraceStage(Guid Id, string Stage, ActivityContext Context);

internal sealed class TraceProbe
{
    public Channel<TraceStage> Stages { get; } = System.Threading.Channels.Channel.CreateUnbounded<TraceStage>();

    public void Record(Guid id, string stage)
        => Stages.Writer.TryWrite(new TraceStage(id, stage, Activity.Current?.Context ?? default));
}

internal sealed class TraceProbeGrpcService(TraceProbe probe, IPublishEndpoint publisher)
    : ConfigurationApi.ConfigurationApiBase
{
    public override async Task<GetReadinessResponse> GetReadiness(GetReadinessRequest request, ServerCallContext context)
    {
        var id = Guid.Parse(context.RequestHeaders.GetValue("probe-id")!);
        probe.Record(id, "grpc");
        await publisher.Publish(new TraceProbeMessage(id), context.CancellationToken);
        return new GetReadinessResponse { Ready = true };
    }
}
