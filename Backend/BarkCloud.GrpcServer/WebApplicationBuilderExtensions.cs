using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Configuration;
using BarkCloud.Shared.Identity;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace BarkCloud.GrpcServer;

public static class WebApplicationBuilderExtensions
{
    public static WebApplicationBuilder SetRunningAddress(this WebApplicationBuilder builder,
        IConfiguration configuration)
    {
        // Listen-порт приоритетно берётся из env (.env → docker-compose: SERVICE_PORT/SERVICE_HTTP1PORT)
        // и переопределяет значение из конфиг-БД, чтобы внешний (nginx/.env) и внутренний порт совпадали.
        var portOverrides = new Dictionary<string, string?>();
        if (int.TryParse(Environment.GetEnvironmentVariable("SERVICE_PORT"), out var envPort) && envPort > 0)
            portOverrides["RunSettings:Port"] = envPort.ToString();
        if (int.TryParse(Environment.GetEnvironmentVariable("SERVICE_HTTP1PORT"), out var envHttp1Port) && envHttp1Port > 0)
            portOverrides["RunSettings:Http1Port"] = envHttp1Port.ToString();
        if (portOverrides.Count > 0)
            builder.Configuration.AddInMemoryCollection(portOverrides);

        builder.Services.AddSettings<RunSettings>(configuration, "RunSettings");

        var runSettings = configuration.GetSection("RunSettings").Get<RunSettings>();

        // Проверяем, что порт задан корректно
        if (runSettings == null || runSettings.Port <= 0)
        {
            var portValue = configuration["RunSettings:Port"];
            throw new InvalidOperationException(
                $"Некорректное значение порта в конфигурации. " +
                $"RunSettings:Port = '{portValue}'. " +
                $"Ожидается числовое значение (например, 7009). " +
                $"Проверьте переменные окружения Docker или файл appsettings.json");
        }

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(runSettings.Port, listenOptions =>
            {
                if (runSettings.Tls != null)
                {
                    listenOptions.UseHttps(runSettings.Tls.Filename, runSettings.Tls.Password);
                }
                listenOptions.Protocols = HttpProtocols.Http2;
            });

            if (runSettings.Http1Port != null)
            {
                options.ListenAnyIP(runSettings.Http1Port.Value, listenOptions =>
                {
                    if (runSettings.Tls != null)
                    {
                        listenOptions.UseHttps(runSettings.Tls.Filename, runSettings.Tls.Password);
                    }
                    listenOptions.Protocols = HttpProtocols.Http1;
                });
            }
        });

        return builder;
    }

    public static WebApplicationBuilder LoadConfiguration(this WebApplicationBuilder builder, ServiceId serviceId)
    {
        var configurationServiceAddress = Environment.GetEnvironmentVariable("CONFIGURATION_SERVICE_URL")
                                          ?? builder.Configuration["ConfigurationServiceAddr"]
                                          ?? "http://localhost:7003";

        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        using var channel = GrpcChannel.ForAddress(configurationServiceAddress);
        var configurationApiClient = new ConfigurationApi.ConfigurationApiClient(channel);

        // Предразделённый bootstrap-ключ для доступа к Configuration (раздаёт секреты).
        // Не приходит из самого Configuration — берётся из окружения, поэтому пригоден на старте.
        var accessKey = Environment.GetEnvironmentVariable("CONFIGURATION_ACCESS_KEY");
        Metadata? headers = null;
        if (!string.IsNullOrEmpty(accessKey))
        {
            headers = new Metadata { { "x-config-access-key", accessKey } };
        }

        var config = configurationApiClient.GetConfiguration(
            new GetConfigurationRequest { ServiceId = (int)serviceId }, headers);

        var configurationDictionary = new Dictionary<string, string>();

        foreach (var configurationItem in config.Configurations)
        {
            var key = configurationItem.Section;

            if (!string.IsNullOrWhiteSpace(configurationItem.Key))
            {
                key += $":{configurationItem.Key}";
            }

            configurationDictionary.Add(key, configurationItem.Value);
        }

        configurationDictionary.Add("ConfigurationServiceAddr", configurationServiceAddress);

        builder.Configuration.AddInMemoryCollection(configurationDictionary);

        return builder;
    }
}