using BarkCloud.Proto.Identity;

using Grpc.Net.ClientFactory;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BarkCloud.GrpcServer.ServiceAuth;public static class ServiceTokenClientExtensions
{
    /// <summary>
    /// Регистрирует клиент ServiceCredentialsApi и <see cref="ServiceTokenProvider"/> для обмена
    /// своего секрета сервиса (ServiceAuth:Credential / env SERVICE_CREDENTIAL) на короткоживущие
    /// сервисные JWT. Секрет обязателен: без него сервис не может вызывать server-API других сервисов.
    /// </summary>
    public static IServiceCollection AddServiceTokenClient(this IServiceCollection services, IConfiguration configuration)
    {
        var identityAddress = configuration["IdentityService:Host"];
        if (string.IsNullOrEmpty(identityAddress))
            throw new InvalidOperationException(
                "IdentityService:Host не задан — не удалось зарегистрировать клиент получения сервисных токенов.");

        var credential = configuration["ServiceAuth:Credential"]
                         ?? Environment.GetEnvironmentVariable("SERVICE_CREDENTIAL");
        if (string.IsNullOrEmpty(credential))
            throw new InvalidOperationException(
                "Не задан секрет сервиса для получения сервисного JWT. Укажите ServiceAuth:Credential " +
                "в конфигурации или переменную окружения SERVICE_CREDENTIAL.");

        services.AddGrpcClient<ServiceCredentialsApi.ServiceCredentialsApiClient>(o =>
        {
            o.Address = new Uri(identityAddress);
        });

        services.AddSingleton(sp => new ServiceTokenProvider(
            sp.GetRequiredService<ServiceCredentialsApi.ServiceCredentialsApiClient>(),
            credential,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<ServiceTokenProvider>>()));

        return services;
    }
}
