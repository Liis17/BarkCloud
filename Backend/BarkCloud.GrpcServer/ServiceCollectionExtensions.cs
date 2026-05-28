using BarkCloud.GrpcServer.Tracker;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BarkCloud.GrpcServer;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSettings<TSettings>(this IServiceCollection services, IConfiguration configuration, string sectionName)
        where TSettings : class
    {
        services.Configure<TSettings>(configuration.GetSection(sectionName));

        services.AddSingleton(provider => provider.GetRequiredService<IOptions<TSettings>>().Value);

        return services;
    }

    /// <summary>
    /// Регистрирует <see cref="RequestContext"/> и его аккессор. Аккессор internal для сборки
    /// BarkCloud.GrpcServer, поэтому хосты других сборок должны регистрировать его через этот метод.
    /// Требует, чтобы в gRPC-пайплайн был добавлен <see cref="Tracker.RequestContextInterceptor"/>.
    /// </summary>
    public static IServiceCollection AddRequestContext(this IServiceCollection services)
    {
        services.AddScoped<IRequestContextAccessor, RequestContextAccessor>();
        services.AddScoped<RequestContext>(sp => sp.GetRequiredService<IRequestContextAccessor>().Current);

        return services;
    }

    public static IServiceCollection AddBarkCloudGrpc(this IServiceCollection services)
    {
        services.AddRequestContext();

        services.AddGrpc(options =>
        {
            options.Interceptors.Add<ServerExceptionInterceptor>();
            options.Interceptors.Add<RequestContextInterceptor>();
        });

        return services;
    }
}