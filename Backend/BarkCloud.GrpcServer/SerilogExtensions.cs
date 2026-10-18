using BarkCloud.GrpcServer.Metrics;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Serilog;
using Serilog.Events;

namespace BarkCloud.GrpcServer;

public static class SerilogExtensions
{
    internal const string LognaraAgentUrl = "http://127.0.0.1:7400/v1/logs";
    private static readonly TimeSpan SeqFlushRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Настраивает Serilog и запускает локальный Lognara-agent, если он включён.
    /// Возвращённый lifetime должен пережить host и освободиться после его остановки.
    /// </summary>
    public static async Task<BarkCloudLoggingLifetime> AddBarkCloudSerilogAsync(
        this WebApplicationBuilder builder,
        string serviceName)
    {
        var startupOptions = LoggingStartupOptions.Capture(builder);
        var seqUrl = builder.Configuration["Seq:ServerUrl"] ?? "http://cloud-seq:5341";
        LognaraAgentRuntime? agent = null;
        Serilog.ILogger? logger = null;
        try
        {
            if (startupOptions.LognaraEnable)
            {
                Directory.CreateDirectory(LognaraAgentRuntime.SpoolDirectory);
                agent = await LognaraAgentRuntime.StartAsync();
            }

            logger = ConfigureLogger(
                new LoggerConfiguration(),
                startupOptions,
                serviceName,
                seqUrl,
                LognaraAgentUrl,
                Path.Combine("logs", "seq-buffer"))
                .CreateLogger();
            Log.Logger = logger;
            builder.Host.UseSerilog(logger, dispose: false);
            builder.Services.Configure<HostOptions>(options =>
                options.ShutdownTimeout = TimeSpan.FromSeconds(30));

            if (agent is not null)
            {
                var ownedAgent = agent;
                builder.Services.AddHostedService<LognaraAgentMonitorService>(services =>
                    new LognaraAgentMonitorService(
                        ownedAgent,
                        services.GetRequiredService<IHostApplicationLifetime>(),
                        services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LognaraAgentMonitorService>>()));
            }

            return new BarkCloudLoggingLifetime(startupOptions.ConsoleOutput, agent);
        }
        catch (Exception startupError)
        {
            Exception? cleanupError = null;
            if (logger is not null)
            {
                try
                {
                    await BarkCloudLoggingLifetime.FlushLoggerAsync(
                        () => Log.CloseAndFlushAsync().AsTask(),
                        BarkCloudLoggingLifetime.FlushTimeout);
                }
                catch (Exception exception)
                {
                    cleanupError = exception;
                }
            }

            try
            {
                if (agent is not null)
                    await agent.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupError ??= exception;
            }

            if (cleanupError is not null)
                throw new AggregateException("BarkCloud logging setup and cleanup failed.", startupError, cleanupError);
            throw;
        }
    }

    internal static LoggerConfiguration ConfigureLogger(
        LoggerConfiguration loggerConfig,
        LoggingStartupOptions startupOptions,
        string serviceName,
        string seqUrl,
        string lognaraAgentUrl,
        string seqBufferBaseFilename)
    {
        loggerConfig
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("Application", serviceName);

        if (startupOptions.SeqEnable)
        {
            loggerConfig.WriteTo.Seq(seqUrl,
                bufferBaseFilename: seqBufferBaseFilename,
                bufferSizeLimitBytes: 104857600,
                batchPostingLimit: 100,
                period: TimeSpan.FromSeconds(2),
                queueSizeLimit: 100000,
                messageHandler: new BoundedHttpMessageHandler(SeqFlushRequestTimeout));
        }

        if (startupOptions.ConsoleOutput)
        {
            loggerConfig.WriteTo.Console(
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
        }

        if (startupOptions.LognaraEnable)
        {
            loggerConfig.WriteTo.Lognara(
                lognaraAgentUrl,
                period: TimeSpan.FromSeconds(2));
        }

        return loggerConfig;
    }

    private sealed class BoundedHttpMessageHandler(TimeSpan timeout)
        : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(timeout);
            return await base.SendAsync(request, requestTimeout.Token);
        }
    }

    /// <summary>
    /// Регистрирует MetricsCollector и MetricsReporterService.
    /// </summary>
    public static IServiceCollection AddBarkCloudMetrics(this IServiceCollection services, string serviceName)
    {
        services.AddSingleton<MetricsCollector>();
        services.AddHostedService(sp =>
            new MetricsReporterService(
                sp.GetRequiredService<MetricsCollector>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MetricsReporterService>>(),
                serviceName));

        return services;
    }
}
