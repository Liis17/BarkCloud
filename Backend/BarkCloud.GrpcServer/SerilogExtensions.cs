using BarkCloud.GrpcServer.Metrics;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Serilog;
using Serilog.Events;

namespace BarkCloud.GrpcServer;

public static class SerilogExtensions
{
    /// <summary>
    /// Настраивает Serilog с выводом в консоль и Seq.
    /// Вызывать ПОСЛЕ LoadConfiguration(), чтобы Seq URL из конфигурации был доступен.
    /// </summary>
    public static WebApplicationBuilder AddBarkCloudSerilog(this WebApplicationBuilder builder, string serviceName)
    {
        var seqUrl = builder.Configuration["Seq:ServerUrl"] ?? "http://cloud-seq:5341";

        builder.Host.UseSerilog((context, loggerConfig) =>
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
                .Enrich.WithProperty("Application", serviceName)
                .WriteTo.Seq(seqUrl,
                    bufferBaseFilename: "logs/seq-buffer",
                    bufferSizeLimitBytes: 104857600,
                    batchPostingLimit: 100,
                    period: TimeSpan.FromSeconds(2),
                    queueSizeLimit: 100000);

            // Console — синхронный sink (пишет в stdout под локом в логирующем потоке).
            // В Production выключаем: логи уходят в Seq (durable-буфер переживает временную
            // недоступность Seq). В Development консоль остаётся для `docker logs` / локальной отладки.
            if (!context.HostingEnvironment.IsProduction())
            {
                loggerConfig.WriteTo.Console(
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
            }
        });

        return builder;
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
