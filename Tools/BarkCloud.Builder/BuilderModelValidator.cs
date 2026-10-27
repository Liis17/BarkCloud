using System.IO;
using System.Text.RegularExpressions;

namespace BarkCloud.Builder;

public static partial class BuilderModelValidator
{
    private static readonly Regex DockerNetworkNamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]*\\z", RegexOptions.Compiled);
    private static readonly Regex DockerContainerNamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]+\\z", RegexOptions.Compiled);
    private static readonly Regex NginxTokenPattern = new("^[A-Za-z0-9*._-]+\\z", RegexOptions.Compiled);
    private static readonly Regex PortPattern = new("^[0-9]+\\z", RegexOptions.Compiled);
    private static readonly Regex LognaraTokenPattern = new("^[A-Za-z0-9._~+/=-]+\\z", RegexOptions.Compiled);
    private static readonly Regex LognaraIdentifierPattern = new("^[A-Za-z0-9._-]+\\z", RegexOptions.Compiled);

    public static IReadOnlyList<string> Validate(BuilderModel model)
    {
        var errors = new List<string>();

        ValidateDockerNetworkName(model.DockerNetworkName, errors);
        ValidateIsolationSettings(model, errors);

        var allNames = model.ContainerNames;
        foreach (var container in allNames.Where(x => x.Enabled))
            ValidateDockerContainerName(container.Name, $"Имя контейнера {container.ServiceAlias}", errors);

        foreach (var group in allNames.Where(x => x.Enabled)
                     .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
                errors.Add($"Имя контейнера «{group.Key}» используется несколькими сервисами.");
        }

        foreach (var container in allNames.Where(x => x.Enabled))
        {
            var conflictingAlias = allNames.FirstOrDefault(x =>
                !string.Equals(x.ServiceAlias, container.ServiceAlias, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.ServiceAlias, container.Name, StringComparison.OrdinalIgnoreCase));
            if (conflictingAlias is not null)
                errors.Add($"Имя контейнера {container.ServiceAlias} «{container.Name}» зарезервировано именем сервиса {conflictingAlias.ServiceAlias}.");
        }

        ValidateLoggingSettings(model, errors);

        if (!model.UseNginx)
            return errors;

        if (model.NginxLocation is not ("Compose" or "External"))
            errors.Add("Выберите размещение nginx: в compose или внешний nginx.");

        if (string.IsNullOrWhiteSpace(model.NginxDomain)
            || !NginxTokenPattern.IsMatch(model.NginxDomain.Trim()))
            errors.Add("Укажите корректный домен nginx без пробелов и управляющих символов.");

        if (model.NginxLocation == "External")
        {
            ValidateExternalCertificatePath(model.ExternalNginxCertificatePath, "Путь к сертификату внешнего nginx", errors);
            ValidateExternalCertificatePath(model.ExternalNginxKeyPath, "Путь к ключу внешнего nginx", errors);
        }
        else if (model.NginxLocation == "Compose")
        {
            ValidateLocalCertificateName(model.CertCrtPath, "Файл сертификата", errors);
            ValidateLocalCertificateName(model.CertKeyPath, "Файл ключа", errors);
        }

        var ports = new List<(string Label, string Value)>
        {
            ("HTTPS nginx", "443"),
            ("Identity", model.IdentityPort),
            ("Users", model.UsersPort),
            ("Files", model.FilesPort),
        };

        ValidateInternalPort(model.FilesHttp1Port, "Files HTTP/1", errors);

        if (model.IncludeTorrent)
        {
            ports.Add(("Torrent", model.TorrentPort));
            ValidateInternalPort(model.TorrentHttp1Port, "Torrent HTTP/1", errors);
        }
        if (model.NginxSeqProxy && model.IncludeSeq)
            ports.Add(("Seq", model.NginxSeqPort));
        if (model.NginxMinioConsoleProxy && model.IncludeMinio)
            ports.Add(("MinIO Console", model.NginxMinioConsolePort));

        var parsedPorts = new List<(string Label, int Port)>();
        foreach (var (label, value) in ports)
        {
            if (!TryParsePort(value, out var port))
                errors.Add($"Порт {label} должен быть числом от 1 до 65535.");
            else
                parsedPorts.Add((label, port));
        }

        foreach (var group in parsedPorts.GroupBy(x => x.Port))
        {
            if (group.Count() > 1)
                errors.Add($"Порт {group.Key} указан для нескольких nginx-сервисов: {string.Join(", ", group.Select(x => x.Label))}.");
        }

        return errors;
    }

    private static void ValidateLoggingSettings(BuilderModel model, ICollection<string> errors)
    {
        foreach (var application in model.LognaraApplications.Where(model.IsApplicationIncluded))
        {
            if (!model.IncludeSeq && application.SeqEnable)
                errors.Add($"Seq отключён, но для {application.DisplayName} включён SeqEnable. Включите Seq или выключите этот канал.");
            if (!model.IncludeLognara && application.LognaraEnable)
                errors.Add($"Lognara отключена, но для {application.DisplayName} включён LognaraEnable. Включите Lognara или выключите этот канал.");
        }

        if (!model.IncludeLognara)
            return;

        ValidateCoreUrl(model.LognaraCoreUrl, errors);
        ValidateAgentToken(model.LognaraCoreToken, "Токен relay для Lognara Core", errors);
        if (!string.IsNullOrWhiteSpace(model.LognaraAgentVersion)
            && !LognaraIdentifierPattern.IsMatch(model.LognaraAgentVersion))
            errors.Add("Версия Lognara agent должна содержать только буквы, цифры, точку, дефис или подчёркивание.");

        var agents = new List<(string Name, string Token, string SpoolPath)>();
        agents.AddRange(model.LognaraApplications
            .Where(x => model.IsApplicationIncluded(x) && x.LognaraEnable)
            .Select(x => (x.DisplayName, LognaraAgentToken.Normalize(x.Token), x.SpoolPath)));
        agents.AddRange(model.LognaraInfrastructure
            .Where(model.IsInfrastructureIncluded)
            .Select(x => (x.DisplayName, LognaraAgentToken.Normalize(x.Token), x.SpoolPath)));

        if (agents.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(model.LognaraServer)
                || !LognaraIdentifierPattern.IsMatch(model.LognaraServer))
                errors.Add("Имя сервера Lognara обязательно и может содержать только буквы, цифры, точку, дефис или подчёркивание.");

            if (string.IsNullOrWhiteSpace(model.LognaraBackend)
                || !LognaraIdentifierPattern.IsMatch(model.LognaraBackend))
                errors.Add("Имя backend Lognara обязательно и может содержать только буквы, цифры, точку, дефис или подчёркивание.");

            if (!string.IsNullOrWhiteSpace(model.LognaraEnvironment)
                && !LognaraIdentifierPattern.IsMatch(model.LognaraEnvironment))
                errors.Add("Окружение Lognara может содержать только буквы, цифры, точку, дефис или подчёркивание.");
        }

        foreach (var agent in agents)
        {
            ValidateAgentToken(agent.Token, $"Токен Lognara для {agent.Name}", errors);
            if (agent.SpoolPath.Any(char.IsControl) || agent.SpoolPath.Contains(':'))
                errors.Add($"Путь Lognara spool для {agent.Name} содержит недопустимые символы.");
        }

        foreach (var duplicate in agents.Where(x => !string.IsNullOrWhiteSpace(x.Token))
                     .GroupBy(x => x.Token, StringComparer.Ordinal)
                     .Where(x => x.Count() > 1))
            errors.Add($"Токен Lognara используется несколькими агентами: {string.Join(", ", duplicate.Select(x => x.Name))}.");

    }

    private static void ValidateCoreUrl(string value, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.AbsolutePath != "/v1/batches"
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || value.Any(char.IsWhiteSpace)
            || value.Any(char.IsControl)
            || value.IndexOfAny(['$', '\'', '"', '#']) >= 0)
            errors.Add("URL Lognara Core обязателен: абсолютный HTTP/HTTPS endpoint /v1/batches без учётных данных, query, #, пробелов и управляющих символов.");
    }

    private static void ValidateIsolationSettings(BuilderModel model, ICollection<string> errors)
    {
        ValidateInternalPort(model.PostgresPort, "PostgreSQL", errors);
        if (string.IsNullOrWhiteSpace(model.PostgresHost) || model.PostgresHost.Any(char.IsControl))
            errors.Add("Укажите адрес PostgreSQL без управляющих символов.");
        if (string.IsNullOrWhiteSpace(model.RabbitHost) || model.RabbitHost.Any(char.IsControl))
            errors.Add("Укажите адрес RabbitMQ без управляющих символов.");
        if (string.IsNullOrWhiteSpace(model.RabbitVirtualHost) || model.RabbitVirtualHost.Any(char.IsControl))
            errors.Add("Укажите vhost RabbitMQ без управляющих символов.");

        if (string.IsNullOrWhiteSpace(model.PostgresUser) || string.IsNullOrWhiteSpace(model.PostgresPassword))
            errors.Add("Учётные данные оператора PostgreSQL обязательны для provisioning.");
        if (model.IncludeRabbitmq && (string.IsNullOrWhiteSpace(model.RabbitUser) || string.IsNullOrWhiteSpace(model.RabbitPass)))
            errors.Add("Bootstrap-учётные данные RabbitMQ обязательны для контейнера брокера.");

        var servicePasswords = new[]
        {
            model.ConfigurationDbPassword, model.IdentityDbPassword, model.UsersDbPassword,
            model.FilesDbPassword, model.TorrentDbPassword, model.IdentityRabbitPassword,
            model.UsersRabbitPassword, model.FilesRabbitPassword, model.TorrentRabbitPassword,
            model.NotificationRabbitPassword
        };
        if (servicePasswords.Any(string.IsNullOrWhiteSpace))
            errors.Add("Все сервисные пароли PostgreSQL и RabbitMQ должны быть непустыми.");
        if (servicePasswords.Any(value => value.Any(char.IsControl)))
            errors.Add("Сервисные пароли PostgreSQL и RabbitMQ не должны содержать управляющие символы.");
    }

    private static void ValidateAgentToken(string value, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !LognaraTokenPattern.IsMatch(value))
            errors.Add($"{label} обязателен и может содержать только буквы, цифры и символы . _ ~ + / = -.");
    }

    private static void ValidateDockerNetworkName(string? value, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || !DockerNetworkNamePattern.IsMatch(value))
            errors.Add("Имя Docker-сети должно начинаться с буквы или цифры и содержать только буквы, цифры, точку, дефис или подчёркивание.");
    }

    private static void ValidateDockerContainerName(string? value, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || !DockerContainerNamePattern.IsMatch(value))
            errors.Add($"{label} должно содержать не менее двух символов, начинаться с буквы или цифры и содержать только буквы, цифры, точку, дефис или подчёркивание.");
    }

    private static void ValidateExternalCertificatePath(string? value, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("/", StringComparison.Ordinal)
            || value.Any(char.IsControl)
            || value.Any(char.IsWhiteSpace)
            || value.IndexOfAny([';', '{', '}', '$', '#', '\\', '"', '\'']) >= 0)
            errors.Add($"{label} должен быть абсолютным Linux-путём внутри nginx-контейнера.");
    }

    private static void ValidateInternalPort(string? value, string label, ICollection<string> errors)
    {
        if (!TryParsePort(value, out _))
            errors.Add($"Внутренний порт {label} должен быть числом от 1 до 65535.");
    }

    private static bool TryParsePort(string? value, out int port)
    {
        port = 0;
        return PortPattern.IsMatch(value ?? "")
            && int.TryParse(value, out port)
            && port is >= 1 and <= 65535;
    }

    private static void ValidateLocalCertificateName(string? path, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || !NginxTokenPattern.IsMatch(name))
            errors.Add($"Имя файла «{label}» содержит недопустимые символы для nginx-конфига.");
    }
}
