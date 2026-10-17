using System.Text.RegularExpressions;

namespace BarkCloud.Web.Infrastructure;

/// <summary>Фактические имена контейнеров при неизменных ключах Compose.</summary>
internal sealed class DockerContainerNames
{
    private static readonly (string Service, string Name)[] Defaults =
    [
        ("configuration", "cloud-configuration"),
        ("identity", "cloud-identity"),
        ("users", "cloud-users"),
        ("files", "cloud-files"),
        ("notification", "cloud-notification"),
        ("torrent", "cloud-torrent"),
        ("web", "cloud-web"),
    ];

    private static readonly HashSet<string> FixedAliases = new(
        [
            "cloud-configuration", "cloud-identity", "cloud-users", "cloud-files",
            "cloud-notification", "cloud-torrent", "cloud-web", "cloud-minio",
            "cloud-rabbitmq", "cloud-postgres", "cloud-seq", "cloud-nginx",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly Regex ValidName = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9_.-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<string, string> _names;

    private DockerContainerNames(Dictionary<string, string> names)
        => _names = names;

    public string For(string service)
        => _names.TryGetValue(service, out var name)
            ? name
            : throw new ArgumentException($"Неизвестный сервис: {service}", nameof(service));

    public static DockerContainerNames FromConfiguration(IConfiguration configuration)
    {
        var names = Defaults.ToDictionary(item => item.Service, item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (service, _) in Defaults)
        {
            var configured = configuration[$"Docker:ContainerNames:{service}"];
            if (configured is not null)
                names[service] = configured;
        }

        Validate(names);
        return new DockerContainerNames(names);
    }

    public static IReadOnlyDictionary<string, string> DefaultValues
        => Defaults.ToDictionary(item => item.Service, item => item.Name, StringComparer.OrdinalIgnoreCase);

    private static void Validate(IReadOnlyDictionary<string, string> names)
    {
        foreach (var (service, name) in names)
        {
            if (!ValidName.IsMatch(name))
                throw new InvalidOperationException(
                    $"Docker:ContainerNames:{service} содержит недопустимое имя контейнера: {name}");

            var defaultName = Defaults.First(item => string.Equals(item.Service, service, StringComparison.OrdinalIgnoreCase)).Name;
            if (FixedAliases.Contains(name) && !string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Docker:ContainerNames:{service} конфликтует с фиксированным DNS-именем {name}");
        }

        var duplicate = names
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Имя Docker-контейнера {duplicate.Key} назначено нескольким сервисам");
    }
}
